using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Composition.Text
{
    // Fonts that ship with a theme (a .ttf or .otf in its folder) rather than being
    // installed on the machine. A theme names its font in FontFamily as usual; if a
    // file in a theme folder has that family name it is used, otherwise the text
    // renderers look the family up on the system as they always have.
    public static class FontFiles
    {
        private class FontFile
        {
            public string Path;
            public int Weight;
            public bool Italic;
        }

        private static Dictionary<string, List<FontFile>> byFamily = new Dictionary<string, List<FontFile>>(StringComparer.OrdinalIgnoreCase);

        public static void AddDirectory(DirectoryInfo directory)
        {
            if (directory == null || !directory.Exists)
                return;

            IEnumerable<FileInfo> files = directory.EnumerateFiles("*.ttf").Concat(directory.EnumerateFiles("*.otf"));
            foreach (FileInfo file in files)
            {
                try
                {
                    using (SKTypeface typeface = SKTypeface.FromFile(file.FullName))
                    {
                        if (typeface == null || string.IsNullOrEmpty(typeface.FamilyName))
                            continue;

                        lock (byFamily)
                        {
                            List<FontFile> list;
                            if (!byFamily.TryGetValue(typeface.FamilyName, out list))
                            {
                                list = new List<FontFile>();
                                byFamily[typeface.FamilyName] = list;
                            }

                            if (!list.Any(f => f.Path == file.FullName))
                            {
                                list.Add(new FontFile() { Path = file.FullName, Weight = typeface.FontWeight, Italic = typeface.FontSlant != SKFontStyleSlant.Upright });
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Not a font we can read; the system fonts still work.
                }
            }
        }

        public static bool Has(string family)
        {
            if (string.IsNullOrEmpty(family))
                return false;

            lock (byFamily)
            {
                return byFamily.ContainsKey(family);
            }
        }

        // The file of this family closest to the weight and slant asked for, or null.
        public static string Find(string family, bool bold, bool italic)
        {
            if (string.IsNullOrEmpty(family))
                return null;

            int weight = bold ? 700 : 400;
            lock (byFamily)
            {
                List<FontFile> list;
                if (!byFamily.TryGetValue(family, out list) || list.Count == 0)
                    return null;

                return list.OrderBy(f => f.Italic == italic ? 0 : 1)
                           .ThenBy(f => Math.Abs(f.Weight - weight))
                           .First().Path;
            }
        }
    }
}
