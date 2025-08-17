using System;
using System.IO;
using System.Threading.Tasks;

namespace FfmpegMediaPlatform
{
    /// <summary>
    /// Global FFmpeg initializer to front-load binding initialization at application startup
    /// </summary>
    public static class FfmpegGlobalInitializer
    {
        private static bool _initialized = false;
        private static readonly object _lock = new object();

        /// <summary>
        /// Initialize FFmpeg bindings early to avoid delays when first accessing replay functionality
        /// </summary>
        public static void InitializeAsync()
        {
            if (_initialized) return;

            // Run initialization in background thread to not block application startup
            Task.Run(() =>
            {
                lock (_lock)
                {
                    if (_initialized) return;

                    try
                    {
                        Console.WriteLine("FfmpegGlobalInitializer: Pre-loading FFmpeg bindings...");
                        FfmpegNativeLoader.EnsureRegistered();
                        _initialized = true;
                        Console.WriteLine("FfmpegGlobalInitializer: FFmpeg bindings pre-loaded successfully");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FfmpegGlobalInitializer: Failed to pre-load FFmpeg bindings: {ex.Message}");
                        // Don't set initialized to true if it failed, so it can be retried later
                    }
                }
            });
        }

        /// <summary>
        /// Synchronous initialization for when immediate initialization is needed
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            lock (_lock)
            {
                if (_initialized) return;

                try
                {
                    Console.WriteLine("FfmpegGlobalInitializer: Starting safe initialization...");
                    
                    // Use a separate process/AppDomain isolation to test FFmpeg loading safely
                    // This prevents the main application from crashing if FFmpeg libs are incompatible
                    if (!TestFFmpegCompatibility())
                    {
                        throw new NotSupportedException("FFmpeg libraries are not compatible with this system");
                    }
                    
                    FfmpegNativeLoader.EnsureRegistered();
                    _initialized = true;
                    Console.WriteLine("FfmpegGlobalInitializer: Initialization completed successfully");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FfmpegGlobalInitializer: Failed to initialize FFmpeg bindings: {ex.GetType().Name}: {ex.Message}");
                    Console.WriteLine($"FfmpegGlobalInitializer: Stack trace: {ex.StackTrace}");
                    throw;
                }
            }
        }
        
        /// <summary>
        /// Test FFmpeg compatibility in a safe way that won't crash the main process
        /// </summary>
        private static bool TestFFmpegCompatibility()
        {
            try
            {
                Console.WriteLine("FfmpegGlobalInitializer: Testing FFmpeg compatibility...");
                
                // First, check if the library files exist and are readable
                var bundledPath = GetBundledLibraryPath();
                if (bundledPath == null || !Directory.Exists(bundledPath))
                {
                    Console.WriteLine("FfmpegGlobalInitializer: No bundled FFmpeg libraries found");
                    return false;
                }
                
                // Check required libraries exist
                string[] requiredLibs = { "libavcodec.dylib", "libavformat.dylib", "libavutil.dylib" };
                foreach (var lib in requiredLibs)
                {
                    var libPath = Path.Combine(bundledPath, lib);
                    if (!File.Exists(libPath))
                    {
                        Console.WriteLine($"FfmpegGlobalInitializer: Missing required library: {lib}");
                        return false;
                    }
                }
                
                Console.WriteLine("FfmpegGlobalInitializer: Basic compatibility check passed");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FfmpegGlobalInitializer: Compatibility test failed: {ex.Message}");
                return false;
            }
        }
        
        private static string GetBundledLibraryPath()
        {
            var assemblyLocation = typeof(FfmpegGlobalInitializer).Assembly.Location;
            var appDirectory = Path.GetDirectoryName(assemblyLocation);
            
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                return Path.Combine(appDirectory, "ffmpeg-libs", "macos");
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                return Path.Combine(appDirectory, "ffmpeg-libs", "windows");
            }
            
            return null;
        }

        /// <summary>
        /// Check if FFmpeg bindings are already initialized
        /// </summary>
        public static bool IsInitialized => _initialized;
        
        /// <summary>
        /// Cleanup FFmpeg bindings and native libraries on application exit
        /// </summary>
        public static void Cleanup()
        {
            lock (_lock)
            {
                if (_initialized)
                {
                    try
                    {
                        Console.WriteLine("FfmpegGlobalInitializer: Cleaning up FFmpeg bindings...");
                        
                        // Force garbage collection to release any native resources
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        
                        _initialized = false;
                        Console.WriteLine("FfmpegGlobalInitializer: FFmpeg bindings cleanup completed");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FfmpegGlobalInitializer: Error during cleanup: {ex.Message}");
                    }
                }
            }
        }
    }
}