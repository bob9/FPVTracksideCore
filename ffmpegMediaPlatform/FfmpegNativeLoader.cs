using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FfmpegMediaPlatform
{
    internal static unsafe class FfmpegNativeLoader
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private static bool registered;
        private static readonly Dictionary<string, IntPtr> handleCache = new();
        private static readonly string[] rootCandidates = new[]
        {
            "/opt/homebrew/opt/ffmpeg/lib",            // system Homebrew FFmpeg first (most compatible)
            "/usr/local/opt/ffmpeg/lib",               // Intel Mac Homebrew path
            "/opt/homebrew/Cellar/ffmpeg/7.1.1_3/lib", // user-provided versioned path
            GetBundledLibraryPath()                    // bundled libraries last (problematic)
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
                Console.WriteLine($"FfmpegNativeLoader.GetBundledLibraryPath: Windows - Using app base directory: {appDirectory}");
            }
            else
            {
                // On Mac/Linux, keep original behavior using assembly location
                appDirectory = Path.GetDirectoryName(assemblyLocation);
                Console.WriteLine($"FfmpegNativeLoader.GetBundledLibraryPath: Mac/Linux - Using assembly directory: {appDirectory}");
            }

            Console.WriteLine($"FfmpegNativeLoader.GetBundledLibraryPath: Assembly location: {assemblyLocation}");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Use unified macOS directory (ARM64 compatible libraries)
                var path = Path.Combine(appDirectory, "ffmpeg-libs", "macos");
                Console.WriteLine($"FfmpegNativeLoader.GetBundledLibraryPath: macOS path: {path} (Architecture: {RuntimeInformation.OSArchitecture})");
                Console.WriteLine($"FfmpegNativeLoader.GetBundledLibraryPath: Directory exists: {Directory.Exists(path)}");
                if (Directory.Exists(path))
                {
                    var files = Directory.GetFiles(path, "*.dylib");
                    Console.WriteLine($"FfmpegNativeLoader.GetBundledLibraryPath: Found {files.Length} dylib files");
                }
                return path;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var path = Path.Combine(appDirectory, "ffmpeg-libs", "windows");
                Console.WriteLine($"FfmpegNativeLoader.GetBundledLibraryPath: Windows path: {path}");
                return path;
            }

            Console.WriteLine("FfmpegNativeLoader.GetBundledLibraryPath: No platform-specific path found");
            return null;
        }

        public static void EnsureRegistered()
        {
            if (registered) return;

            Console.WriteLine("FfmpegNativeLoader.EnsureRegistered: Starting registration...");
            Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: Platform = {RuntimeInformation.OSDescription}");
            Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: Architecture = {RuntimeInformation.OSArchitecture}");

            // IMPORTANT: Set the bundled library path for FFmpeg.AutoGen BEFORE any FFmpeg functions are called
            var bundledPath = GetBundledLibraryPath();
            Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: Bundled path: {bundledPath}");

            if (bundledPath != null && Directory.Exists(bundledPath))
            {
                Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: Directory exists - Setting ffmpeg.RootPath to: {bundledPath}");
                Console.WriteLine($"Current process architecture: {RuntimeInformation.ProcessArchitecture}");
                Console.WriteLine($"Current OS architecture: {RuntimeInformation.OSArchitecture}");
                
                // List files in the directory for debugging
                var files = Directory.GetFiles(bundledPath, "*.dylib");
                Console.WriteLine($"Found {files.Length} dylib files in directory:");
                foreach (var file in files.Take(5)) // Show first 5 files
                {
                    Console.WriteLine($"  - {Path.GetFileName(file)}");
                }
                
                ffmpeg.RootPath = bundledPath;
                Console.WriteLine($"FFmpeg native libraries loaded from: {bundledPath}");

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
                    Console.WriteLine($"  {lib}: {(exists ? "EXISTS" : "MISSING")}");
                    if (!exists) allLibsExist = false;
                }

                // Check dependency libraries (Windows only)
                foreach (var lib in dependencyLibs)
                {
                    var libPath = Path.Combine(bundledPath, lib);
                    bool exists = File.Exists(libPath);
                    Console.WriteLine($"  {lib} (dependency): {(exists ? "EXISTS" : "MISSING")}");
                    if (!exists) allLibsExist = false;
                }

                if (!allLibsExist)
                {
                    throw new FileNotFoundException("Required FFmpeg library dependencies are missing. Please ensure all FFmpeg libraries and dependencies are present.");
                }

                // Set FFmpeg.AutoGen root path explicitly
                ffmpeg.RootPath = bundledPath;
                Console.WriteLine($"Set ffmpeg.RootPath to: {ffmpeg.RootPath}");

                // Add bundled path to PATH environment variable for dependency resolution
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!currentPath.Contains(bundledPath))
                {
                    string pathSeparator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";
                    Environment.SetEnvironmentVariable("PATH", bundledPath + pathSeparator + currentPath);
                    Console.WriteLine($"Added to PATH: {bundledPath}");
                }

                // Pre-load FFmpeg libraries in dependency order to avoid issues (Windows only)
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        Console.WriteLine("Pre-loading FFmpeg libraries in dependency order (Windows)...");

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
                                    Console.WriteLine($"Pre-loaded: {Path.GetFileName(libPath)} -> Handle: {handle}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Failed to pre-load {Path.GetFileName(libPath)}: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Pre-loading libraries failed: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine("Skipping library pre-loading on macOS (not needed with FFmpeg.AutoGen)");
                }

                // Try a different approach: manually ensure library loading
                try
                {
                    Console.WriteLine("Attempting manual library loading with System.Runtime.InteropServices...");
                    
                    // Try to manually load the libraries first
                    var libAvUtilPath = Path.Combine(bundledPath, "libavutil.dylib");
                    var libAvCodecPath = Path.Combine(bundledPath, "libavcodec.dylib");
                    var libAvFormatPath = Path.Combine(bundledPath, "libavformat.dylib");
                    
                    Console.WriteLine($"Manually loading: {libAvUtilPath}");
                    var utilHandle = NativeLibrary.Load(libAvUtilPath);
                    Console.WriteLine($"libavutil loaded: {utilHandle}");
                    
                    Console.WriteLine($"Manually loading: {libAvCodecPath}");
                    var codecHandle = NativeLibrary.Load(libAvCodecPath);
                    Console.WriteLine($"libavcodec loaded: {codecHandle}");
                    
                    Console.WriteLine($"Manually loading: {libAvFormatPath}");
                    var formatHandle = NativeLibrary.Load(libAvFormatPath);
                    Console.WriteLine($"libavformat loaded: {formatHandle}");
                    
                    // Now set the FFmpeg.AutoGen path
                    ffmpeg.RootPath = bundledPath;
                    Console.WriteLine($"FFmpeg.AutoGen RootPath set to: {ffmpeg.RootPath}");
                    
                    // Try a test function call
                    Console.WriteLine("Testing av_version_info after manual loading...");
                    var version = ffmpeg.av_version_info();
                    Console.WriteLine($"SUCCESS: FFmpeg version: {version}");
                    
                    // Try to register device formats to enable protocols
                    Console.WriteLine("Registering device formats and protocols...");
                    try
                    {
                        ffmpeg.avdevice_register_all();
                        Console.WriteLine("SUCCESS: Device formats and protocols registered");
                    }
                    catch (Exception regEx)
                    {
                        Console.WriteLine($"Device registration failed: {regEx.Message}");
                        Console.WriteLine("Will attempt to use AVFoundation anyway - the protocol might still work");
                    }
                }
                catch (Exception manualEx)
                {
                    Console.WriteLine($"Manual library loading failed: {manualEx.Message}");
                    Console.WriteLine("Falling back to standard FFmpeg.AutoGen initialization...");
                    
                    ffmpeg.RootPath = bundledPath;
                    Console.WriteLine($"FFmpeg.AutoGen RootPath set to: {ffmpeg.RootPath}");
                    Console.WriteLine("WARNING: Native FFmpeg may have compatibility issues.");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: Bundled path failed, trying system paths...");
                Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: bundledPath = {bundledPath}");
                
                // Try alternative paths for Mac
                bool foundWorkingPath = false;
                foreach (var root in rootCandidates)
                {
                    Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: Checking path: {root}");
                    if (root != null && Directory.Exists(root))
                    {
                        try
                        {
                            Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: Testing path: {root}");
                            ffmpeg.RootPath = root;
                            
                            // Test with a simple function call
                            var testVersion = ffmpeg.av_version_info();
                            Console.WriteLine($"FFmpeg native libraries loaded successfully from: {root} (version: {testVersion})");
                            foundWorkingPath = true;
                            break;
                        }
                        catch (Exception testEx)
                        {
                            Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: Path {root} failed test: {testEx.Message}");
                            continue;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: Path does not exist: {root}");
                    }
                }
                
                if (!foundWorkingPath)
                {
                    throw new NotSupportedException("No working FFmpeg libraries found in any of the candidate paths. Please install FFmpeg via Homebrew: brew install ffmpeg");
                }
            }
            else
            {
                Console.WriteLine($"FfmpegNativeLoader.EnsureRegistered: No bundled FFmpeg libraries found for platform: {RuntimeInformation.OSDescription}");
                throw new PlatformNotSupportedException("FFmpeg libraries not found. Please ensure the appropriate FFmpeg libraries are included in the application package.");
            }

            registered = true;
            Console.WriteLine("FfmpegNativeLoader.EnsureRegistered: Registration completed");
        }
    }
} 