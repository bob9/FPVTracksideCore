using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tools
{
    public class TextureCache : IDisposable
    {
        private Dictionary<string, Texture2D> stringToTexture;

        public GraphicsDevice GraphicsDevice { get; private set; }

        public bool PreMultipliedAlpha { get; private set; }

        // A folder whose images replace the built-in ones in img/ (set by the theme).
        public static string ImageOverrideDirectory { get; set; }

        public TextureCache(GraphicsDevice device, bool preMultipliedAlpha)
        {
            GraphicsDevice = device;
            PreMultipliedAlpha = preMultipliedAlpha;
            stringToTexture = new Dictionary<string, Texture2D>();
        }

        public void Dispose()
        {
            foreach (Texture2D texture2D in stringToTexture.Values)
            {
                texture2D.Dispose();
            }
            stringToTexture.Clear();
        }

        public Texture2D GetTextureFromColor(Color color)
        {
            if (stringToTexture == null)
            {
                stringToTexture = new Dictionary<string, Texture2D>();
            }

            string colorString = color.ToString();

            Texture2D texture;
            lock (stringToTexture)
            {
                if (!stringToTexture.TryGetValue(colorString, out texture))
                {
                    texture = TextureHelper.CreateTextureFromColor(GraphicsDevice, color);
                    stringToTexture.Add(colorString, texture);
                }
            }
            return texture;
        }

        // A white rounded rectangle for drawing as nine slices: corners of radius
        // `radius`, an optional ring `ring` pixels wide instead of a fill, and an
        // optional soft edge `blur` pixels wide (for shadows). Each corner is
        // radius + blur pixels square and the middle is two pixels, so the
        // texture is drawn with its corners at one to one and its edges stretched.
        public Texture2D GetShapeTexture(int radius, int ring, int blur)
        {
            string key = "shape:" + radius + ":" + ring + ":" + blur;

            Texture2D texture;
            lock (stringToTexture)
            {
                if (!stringToTexture.TryGetValue(key, out texture))
                {
                    texture = TextureHelper.CreateShapeTexture(GraphicsDevice, radius, ring, blur);
                    stringToTexture.Add(key, texture);
                }
            }
            return texture;
        }

        public Texture2D GetTextureFromFilename(string filename, bool forceRefresh)
        {
            return GetTextureFromFilename(filename, Color.Transparent, forceRefresh);
        }

        public Texture2D GetTextureFromFilename(string filename, Color fallback, bool forceRefresh)
        {
            return GetTextureFromFilename(filename, fallback, forceRefresh, PreMultipliedAlpha);
        }

        public Texture2D GetTextureFromFilename(string filename, Color fallback, bool forceRefresh, bool preMultiplyAlpha)
        {
            if (stringToTexture == null)
            {
                stringToTexture = new Dictionary<string, Texture2D>();
            }

            // mac compatibility.
            filename = filename.Replace("\\", "/");

            // The current theme's own copy of a built-in image, if it has one.
            if (ImageOverrideDirectory != null && filename.StartsWith("img/", StringComparison.OrdinalIgnoreCase))
            {
                string themed = System.IO.Path.Combine(ImageOverrideDirectory, filename.Substring(4));
                if (System.IO.File.Exists(themed))
                {
                    filename = themed;
                }
            }

            // Relative paths (img/..., themes/... etc) are relative to the working
            // directory, not to the current directory, which on macOS points inside
            // the app bundle.
            filename = IOTools.ResolveFromWorkingDirectory(filename);

            Texture2D texture;
            lock (stringToTexture)
            {
                if (forceRefresh)
                {
                    if (stringToTexture.ContainsKey(filename))
                    {
                        stringToTexture.Remove(filename);
                    }
                }

                if (!stringToTexture.TryGetValue(filename, out texture))
                {
                    try
                    {
                        texture = TextureHelper.LoadTexture(GraphicsDevice, filename, preMultiplyAlpha);
                        if (texture != null)
                        {
                            stringToTexture.Add(filename, texture);
                        }
                    }
                    catch
                    {
                        return GetTextureFromColor(fallback);
                    }
                }
            }
            return texture;
        }
    }
}
