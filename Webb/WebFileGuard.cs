using System;
using System.IO;
using System.Linq;

namespace Webb
{
    // Which files on disk the web server may hand out.
    //
    // The web server serves files from FPVTrackside's folder by URL (the old web
    // page, themes, pilot photos, the event JSON). That folder also holds things
    // nobody on the network should read: the profile settings (OBS password,
    // timing settings), the logs, and WebUsers.json (password hashes).
    public static class WebFileGuard
    {
        // Top-level folders that are never served.
        private static readonly string[] privateFolders = { "data", "log" };

        // The resolved path is inside root. Stops "C:\..." and "..\" style
        // segments escaping the folder (Path.Combine takes a rooted segment as-is).
        public static bool IsInside(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
                return false;

            string full = Path.GetFullPath(path);
            string top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            StringComparison cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return full.Equals(top, cmp) || full.StartsWith(top + Path.DirectorySeparatorChar, cmp);
        }

        // A file under the working directory that may be served.
        public static bool MayServe(string path, string root)
        {
            if (!IsInside(path, root))
                return false;

            string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            if (privateFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
                return false;

            string name = parts[parts.Length - 1];
            if (name.StartsWith(Api.ApiUsers.FileName, StringComparison.OrdinalIgnoreCase))
                return false;

            // Settings files. Themes are the only XML a web page has a reason to read.
            if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !parts[0].Equals("themes", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
    }
}
