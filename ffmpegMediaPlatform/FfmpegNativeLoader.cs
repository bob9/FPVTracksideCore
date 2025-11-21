using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FfmpegMediaPlatform
{
    internal static class FfmpegNativeLoader
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private static bool registered;
        private static readonly Dictionary<string, IntPtr> handleCache = new();
        private static readonly string[] rootCandidates = new[]
        {
            GetBundledLibraryPath(),                   // bundled libraries first
            "/opt/homebrew/Cellar/ffmpeg/7.1.1_3/lib", // user-provided versioned path
            "/opt/homebrew/opt/ffmpeg/lib"             // stable symlink
        };

        private static string GetBundledLibraryPath()
        {
            var assemblyLocation = typeof(FfmpegNativeLoader).Assembly.Location;
            string appDirectory;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Windows, use the main application's base directory instead of assembly location
                // This ensures we look where the FFmpeg libraries are actually copied (next to the main executable)
                appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.GetBundledLibraryPath: Windows - Using app base directory: {appDirectory}");
            }
            else
            {
                // On Mac/Linux, keep original behavior using assembly location
                appDirectory = Path.GetDirectoryName(assemblyLocation);
                Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.GetBundledLibraryPath: Mac/Linux - Using assembly directory: {appDirectory}");
            }

            Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.GetBundledLibraryPath: Assembly location: {assemblyLocation}");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Use unified macOS directory (ARM64 compatible libraries)
                var path = Path.Combine(appDirectory, "ffmpeg-libs", "macos");
                Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.GetBundledLibraryPath: macOS path: {path} (Architecture: {RuntimeInformation.OSArchitecture})");
                Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.GetBundledLibraryPath: Directory exists: {Directory.Exists(path)}");
                if (Directory.Exists(path))
                {
                    var files = Directory.GetFiles(path, "*.dylib");
                    Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.GetBundledLibraryPath: Found {files.Length} dylib files");
                }
                return path;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var path = Path.Combine(appDirectory, "ffmpeg-libs", "windows");
                Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.GetBundledLibraryPath: Windows path: {path}");
                return path;
            }

            Tools.Logger.VideoLog.LogDebugStatic("FfmpegNativeLoader.GetBundledLibraryPath: No platform-specific path found");
            return null;
        }

        public static void EnsureRegistered()
        {
            if (registered) return;

            Tools.Logger.VideoLog.LogDebugStatic("FfmpegNativeLoader.EnsureRegistered: Starting registration...");
            Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.EnsureRegistered: Platform = {RuntimeInformation.OSDescription}");
            Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.EnsureRegistered: Architecture = {RuntimeInformation.OSArchitecture}");

            // IMPORTANT: Set the bundled library path for FFmpeg.AutoGen BEFORE any FFmpeg functions are called
            var bundledPath = GetBundledLibraryPath();
            Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.EnsureRegistered: Bundled path: {bundledPath}");

            if (bundledPath != null && Directory.Exists(bundledPath))
            {
                Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.EnsureRegistered: Directory exists - Setting ffmpeg.RootPath to: {bundledPath}");
                Tools.Logger.VideoLog.LogDebugStatic($"Current process architecture: {RuntimeInformation.ProcessArchitecture}");
                Tools.Logger.VideoLog.LogDebugStatic($"Current OS architecture: {RuntimeInformation.OSArchitecture}");

                // List files in the directory for debugging
                var files = Directory.GetFiles(bundledPath, "*.dylib");
                Tools.Logger.VideoLog.LogDebugStatic($"Found {files.Length} dylib files in directory:");
                foreach (var file in files.Take(5)) // Show first 5 files
                {
                    Tools.Logger.VideoLog.LogDebugStatic($"  - {Path.GetFileName(file)}");
                }

                // Set FFmpeg.AutoGen to use bundled libraries
                ffmpeg.RootPath = bundledPath;
                Tools.Logger.VideoLog.LogDebugStatic($"FFmpeg native libraries path set to: {bundledPath}");

                // Set error handling for function resolution
                DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                Tools.Logger.VideoLog.LogDebugStatic($"ThrowErrorIfFunctionNotFound = true");

                // Check if essential libraries exist (platform-specific)
                string[] requiredLibs;
                string[] dependencyLibs = new string[0]; // Initialize empty for non-Windows

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    requiredLibs = new[] { "avcodec-61.dll", "avformat-61.dll", "avutil-59.dll", "swscale-8.dll", "swresample-5.dll" };
                    dependencyLibs = new[] { "libiconv-2.dll", "libwinpthread-1.dll", "zlib1.dll" };
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    requiredLibs = new[] { "libavcodec.dylib", "libavformat.dylib", "libavutil.dylib", "libswscale.dylib", "libswresample.dylib" };
                }
                else
                {
                    requiredLibs = new[] { "libavcodec.so", "libavformat.so", "libavutil.so", "libswscale.so", "libswresample.so" };
                }

                bool allLibsExist = true;
                foreach (var lib in requiredLibs)
                {
                    var libPath = Path.Combine(bundledPath, lib);
                    bool exists = File.Exists(libPath);
                    Tools.Logger.VideoLog.LogDebugStatic($"  {lib}: {(exists ? "EXISTS" : "MISSING")}");
                    if (!exists) allLibsExist = false;
                }

                // Check dependency libraries (Windows only)
                foreach (var lib in dependencyLibs)
                {
                    var libPath = Path.Combine(bundledPath, lib);
                    bool exists = File.Exists(libPath);
                    Tools.Logger.VideoLog.LogDebugStatic($"  {lib} (dependency): {(exists ? "EXISTS" : "MISSING")}");
                    if (!exists) allLibsExist = false;
                }

                if (!allLibsExist)
                {
                    throw new FileNotFoundException("Required FFmpeg library dependencies are missing. Please ensure all FFmpeg libraries and dependencies are present.");
                }

                // Set FFmpeg.AutoGen root path explicitly
                ffmpeg.RootPath = bundledPath;
                Tools.Logger.VideoLog.LogDebugStatic($"Set ffmpeg.RootPath to: {ffmpeg.RootPath}");

                // Add bundled path to PATH environment variable for dependency resolution
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!currentPath.Contains(bundledPath))
                {
                    string pathSeparator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";
                    Environment.SetEnvironmentVariable("PATH", bundledPath + pathSeparator + currentPath);
                    Tools.Logger.VideoLog.LogDebugStatic($"Added to PATH: {bundledPath}");
                }

                // Pre-load FFmpeg libraries in dependency order to avoid issues (Windows only)
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        Tools.Logger.VideoLog.LogDebugStatic("Pre-loading FFmpeg libraries in dependency order (Windows)...");

                        // Load libraries in dependency order
                        var libPaths = new[]
                        {
                            Path.Combine(bundledPath, "avutil-59.dll"),
                            Path.Combine(bundledPath, "swresample-5.dll"),
                            Path.Combine(bundledPath, "swscale-8.dll"),
                            Path.Combine(bundledPath, "avcodec-61.dll"),
                            Path.Combine(bundledPath, "avformat-61.dll"),
                            Path.Combine(bundledPath, "avfilter-10.dll"),
                            Path.Combine(bundledPath, "avdevice-61.dll")
                        };

                        foreach (var libPath in libPaths)
                        {
                            if (File.Exists(libPath))
                            {
                                try
                                {
                                    var handle = LoadLibrary(libPath);
                                    Tools.Logger.VideoLog.LogDebugStatic($"Pre-loaded: {Path.GetFileName(libPath)} -> Handle: {handle}");
                                }
                                catch (Exception ex)
                                {
                                    Tools.Logger.VideoLog.LogException($"Failed to pre-load {Path.GetFileName(libPath)}", ex);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger.VideoLog.LogException($"Pre-loading libraries failed", ex);
                    }
                }
                else
                {
                    Tools.Logger.VideoLog.LogDebugStatic("Skipping library pre-loading on macOS (not needed with FFmpeg.AutoGen)");
                }

                // Force FFmpeg.AutoGen to initialize with the new path
                try
                {
                    Tools.Logger.VideoLog.LogDebugStatic("Testing FFmpeg.AutoGen 8.0 initialization...");
                    Tools.Logger.VideoLog.LogDebugStatic($"ffmpeg.RootPath is set to: {ffmpeg.RootPath}");

                    // Test each function individually to find which one fails
                    try
                    {
                        Tools.Logger.VideoLog.LogDebugStatic("Testing av_version_info()...");
                        var version = ffmpeg.av_version_info();
                        Tools.Logger.VideoLog.LogDebugStatic($"✅ av_version_info() worked: {version}");
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger.VideoLog.LogException($"❌ av_version_info() failed", ex);
                        throw;
                    }

                    try
                    {
                        Tools.Logger.VideoLog.LogDebugStatic("Testing avcodec_version()...");
                        var codecVersion = ffmpeg.avcodec_version();
                        Tools.Logger.VideoLog.LogDebugStatic($"✅ avcodec_version() worked: {codecVersion}");
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger.VideoLog.LogException($"❌ avcodec_version() failed", ex);
                        throw;
                    }

                    try
                    {
                        Tools.Logger.VideoLog.LogDebugStatic("Testing avformat_version()...");
                        var formatVersion = ffmpeg.avformat_version();
                        Tools.Logger.VideoLog.LogDebugStatic($"✅ avformat_version() worked: {formatVersion}");
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger.VideoLog.LogException($"❌ avformat_version() failed", ex);
                        throw;
                    }

                    Tools.Logger.VideoLog.LogDebugStatic("✅ FFmpeg 8.0 initialization successful!");
                }
                catch (Exception ex)
                {
                    Tools.Logger.VideoLog.LogException($"FFmpeg initialization test failed", ex);
                    throw new NotSupportedException($"FFmpeg 8.0 initialization failed: {ex.Message}", ex);
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.EnsureRegistered: Bundled path not found or doesn't exist");
                Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.EnsureRegistered: bundledPath = {bundledPath}");
                Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.EnsureRegistered: Directory.Exists = {(bundledPath != null ? Directory.Exists(bundledPath) : "bundledPath is null")}");
                
                // Fallback to system paths for Mac
                Tools.Logger.VideoLog.LogDebugStatic("FfmpegNativeLoader.EnsureRegistered: Trying system paths...");
                foreach (var root in rootCandidates.Skip(1)) // Skip the bundled path
                {
                    Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.EnsureRegistered: Checking system path: {root}");
                    if (Directory.Exists(root))
                    {
                        ffmpeg.RootPath = root;
                        Tools.Logger.VideoLog.LogStatic($"FFmpeg native libraries loaded from system path: {root}");
                        break;
                    }
                    else
                    {
                        Tools.Logger.VideoLog.LogDebugStatic($"FfmpegNativeLoader.EnsureRegistered: System path does not exist: {root}");
                    }
                }
            }
            else
            {
                Tools.Logger.VideoLog.LogCall($"FfmpegNativeLoader.EnsureRegistered: No bundled FFmpeg libraries found for platform: {RuntimeInformation.OSDescription}");
                throw new PlatformNotSupportedException("FFmpeg libraries not found. Please ensure the appropriate FFmpeg libraries are included in the application package.");
            }

            registered = true;
            Tools.Logger.VideoLog.LogDebugStatic("FfmpegNativeLoader.EnsureRegistered: Registration completed");
        }
    }
} 