using System;
using System.IO;

namespace FfmpegMediaPlatform
{
    /// <summary>
    /// Feature flags for FFmpeg functionality
    /// </summary>
    public static class FfmpegFeatureFlags
    {
        /// <summary>
        /// Enable/disable native FFmpeg library usage
        /// Can be controlled by environment variable FFMPEG_USE_NATIVE_LIBS
        /// </summary>
        public static bool UseNativeLibraries
        {
            get
            {
                // Check environment variable first
                var envVar = Environment.GetEnvironmentVariable("FFMPEG_USE_NATIVE_LIBS");
                if (bool.TryParse(envVar, out bool envValue))
                {
                    Console.WriteLine($"FfmpegFeatureFlags: Using environment variable FFMPEG_USE_NATIVE_LIBS = {envValue}");
                    return envValue;
                }

                // Check for a feature flag file
                var flagFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "enable_native_ffmpeg.flag");
                if (File.Exists(flagFile))
                {
                    Console.WriteLine($"FfmpegFeatureFlags: Found flag file, enabling native libraries");
                    return true;
                }

                // Default: enabled for testing direct AVFoundation access approach
                Console.WriteLine($"FfmpegFeatureFlags: Native libraries enabled - testing direct access method");
                return true;
            }
        }

        /// <summary>
        /// Create the flag file to enable native libraries
        /// </summary>
        public static void EnableNativeLibraries()
        {
            var flagFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "enable_native_ffmpeg.flag");
            try
            {
                File.WriteAllText(flagFile, $"Native FFmpeg enabled at {DateTime.Now}");
                Console.WriteLine($"FfmpegFeatureFlags: Created flag file: {flagFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FfmpegFeatureFlags: Failed to create flag file: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove the flag file to disable native libraries
        /// </summary>
        public static void DisableNativeLibraries()
        {
            var flagFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "enable_native_ffmpeg.flag");
            try
            {
                if (File.Exists(flagFile))
                {
                    File.Delete(flagFile);
                    Console.WriteLine($"FfmpegFeatureFlags: Removed flag file: {flagFile}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FfmpegFeatureFlags: Failed to remove flag file: {ex.Message}");
            }
        }
    }
}