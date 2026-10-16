using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Graphics;
using Tools;

namespace Composition.Nodes
{
    public class ImageNode : AspectNode
    {
        public Texture2D Texture
        {
            get
            {
                return texture;
            }
            set
            {
                if (value != null)
                {
                    texture = value;
                    RequestRedraw();
                }
                else
                {
                    texture = value;
                }
            }
        }

        protected Texture2D texture;

        public RectangleF RelativeSourceBounds { get; set; }
        public Rectangle SourceBounds { get; set; }

        public string FileName { get; protected set; }
        public Color Tint { get; set; }

        protected bool sharedTexture;

        public bool CanScale { get; set; }

        public bool CropToFit { get; set; }

        public bool ReloadFromFile { get; set; }

        // Draw the image as nine slices: the outer Slice pixels of the source are
        // corners and edges that keep their size (scaled by SliceScale) and only
        // the middle stretches, so a rounded image stays round at any size.
        public int Slice { get; set; }
        public float SliceScale { get; set; }

        public ImageNode()
        {
            ReloadFromFile = false;
            CanScale = true;
            CropToFit = false;
            sharedTexture = false;
            Tint = Color.White;
            SliceScale = 1;
            RelativeSourceBounds = new RectangleF(0, 0, 1, 1);
            SourceBounds = new Rectangle();
        }

        public ImageNode(Texture2D texture, bool sharedTexture)
            : this()
        {
            this.sharedTexture = sharedTexture;
            Texture = texture;
            if (Texture != null)
            {
                SourceBounds = new Rectangle(0, 0, Texture.Width, Texture.Height);
            }
            UpdateAspectRatioFromTexture();
        }

        public ImageNode(string filename)
            :this()
        {
            FileName = filename;
        }

        public ImageNode(string filename, Rectangle sourceBounds)
            : this()
        {
            SourceBounds = sourceBounds;
            FileName = filename;
        }

        public ImageNode(string filename, Color tint)
           : this()
        {
            Tint = tint;
            FileName = filename;
        }

        public override void Dispose()
        {
            if (!sharedTexture && texture != null)
            {
                texture.Dispose();
                texture = null;
            }
            base.Dispose();
        }

        public override RectangleF CalculateRelativeBounds(RectangleF parentPosition)
        {
            RectangleF bounds = base.CalculateRelativeBounds(parentPosition);
            if (!CanScale && texture != null)
            {
                return Maths.FitBoxMaintainAspectRatio(BaseBoundsF, new RectangleF(0, 0, texture.Width, texture.Height), 1, Alignment);
            }
            return bounds;
        }

        public virtual void UpdateAspectRatioFromTexture()
        {
            if (Texture != null)
            {
                float oldAspect = AspectRatio;
                SetAspectRatio(Texture.GetSize());
                if (oldAspect != AspectRatio)
                {
                    RequestLayout();
                }
            }
        }

        public override void Layout(RectangleF parentBounds)
        {
            base.Layout(parentBounds);

            Texture2D temp = texture;
            if (temp != null)
            {
                if (CropToFit || RelativeSourceBounds.X != 0 ||
                    RelativeSourceBounds.Y != 0 ||
                    RelativeSourceBounds.Width != 1 ||
                    RelativeSourceBounds.Height != 1)
                {
                    RectangleF sourceBounds = new RectangleF();
                    sourceBounds.X = (int)(temp.Width * RelativeSourceBounds.X);
                    sourceBounds.Y = (int)(temp.Height * RelativeSourceBounds.Y);
                    sourceBounds.Width = (int)(temp.Width * RelativeSourceBounds.Width);
                    sourceBounds.Height = (int)(temp.Height * RelativeSourceBounds.Height);

                    if (CropToFit)
                    {
                        sourceBounds = Maths.FitBoxMaintainAspectRatio(sourceBounds, BaseBoundsF, Alignment, FitType);
                    }

                    SourceBounds = sourceBounds.ToRectangle();
                }
            }
        }

        public void FlipX()
        {
            RelativeSourceBounds = new RectangleF(1, 0, -1, 1);
        }

        public void FlipY()
        {
            RelativeSourceBounds = new RectangleF(0, 0, 1, 1);
        }

        public virtual void LoadImage(Drawer id)
        {
            try
            {
                texture = id.TextureCache.GetTextureFromFilename(FileName, ReloadFromFile);
                if (SourceBounds.Width == 0 || SourceBounds.Height == 0)
                {
                    SourceBounds = new Rectangle(0, 0, texture.Width, texture.Height);
                }
                sharedTexture = true;
                UpdateAspectRatioFromTexture();
                ReloadFromFile = false;
            }
            catch
            {
                FileName = null;
                texture = null;
            }
        }

        public override void Draw(Drawer id, float parentAlpha)
        {
            DebugTimer.DebugStartTime(this);

            if ((texture == null || ReloadFromFile) && !string.IsNullOrEmpty(FileName))
            {
                LoadImage(id);
            }

            if (texture != null)
            {
                float alpha = parentAlpha * Alpha;
                if (Tint.A != 255)
                {
                    alpha *= Tint.A / 255.0f;
                }

                try
                {
                    DrawImage(id, alpha);
                }
                catch
                {
                    if (!sharedTexture && texture != null)
                    {
                        texture.Dispose();
                    }
                    Texture = null;
                }
            }

            DebugTimer.DebugEndTime(this);

            base.Draw(id, parentAlpha);
        }

        protected virtual void DrawImage(Drawer id, float alpha)
        {
            if (Slice > 0)
            {
                DrawSliced(id, texture, SourceBounds, BoundsF, Slice, Slice * SliceScale * ShapeStyle.Scale(id), Tint, alpha);
            }
            else
            {
                id.Draw(texture, SourceBounds, Bounds, Tint, alpha);
            }
        }

        public static void DrawSliced(Drawer id, Texture2D texture, Rectangle source, RectangleF destF, int slice, float sliceScreen, Color tint, float alpha)
        {
            Rectangle dest = new Rectangle((int)Math.Round(destF.X), (int)Math.Round(destF.Y), (int)Math.Round(destF.Width), (int)Math.Round(destF.Height));

            int s = Math.Min(slice, Math.Min(source.Width, source.Height) / 2);
            int d = (int)Math.Round(Math.Min(sliceScreen, Math.Min(dest.Width, dest.Height) / 2.0f));
            if (s <= 0 || d <= 0)
            {
                id.Draw(texture, source, dest, tint, alpha);
                return;
            }

            int[] sx = { source.X, source.X + s, source.Right - s, source.Right };
            int[] sy = { source.Y, source.Y + s, source.Bottom - s, source.Bottom };
            int[] dx = { dest.X, dest.X + d, dest.Right - d, dest.Right };
            int[] dy = { dest.Y, dest.Y + d, dest.Bottom - d, dest.Bottom };

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    Rectangle src = new Rectangle(sx[col], sy[row], sx[col + 1] - sx[col], sy[row + 1] - sy[row]);
                    Rectangle dst = new Rectangle(dx[col], dy[row], dx[col + 1] - dx[col], dy[row + 1] - dy[row]);
                    if (src.Width <= 0 || src.Height <= 0 || dst.Width <= 0 || dst.Height <= 0)
                        continue;
                    id.Draw(texture, src, dst, tint, alpha);
                }
            }
        }

        public void SetFilename(string filename)
        {
            if (!sharedTexture && texture != null)
            {
                texture.Dispose();
            }

            texture = null;
            FileName = filename;
        }

        public virtual void SetToolTexture(ToolTexture tt)
        {
            SetFilename(tt.TextureFilename);
            SourceBounds = tt.Region;
            Slice = tt.Slice;
        }
    }
}
