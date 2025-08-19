using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using ImageServer;
using Microsoft.Xna.Framework.Graphics;
using Tools;

namespace FfmpegMediaPlatform
{
    public unsafe class FfmpegLibAvFoundationFrameSource : TextureFrameSource, ICaptureFrameSource
    {
        // Static constructor disabled - initialization moved to constructor for better error handling
        static FfmpegLibAvFoundationFrameSource()
        {
            Console.WriteLine("FfmpegLibAvFoundationFrameSource: Static constructor called - deferring initialization");
        }

        private AVFormatContext* fmt;
        private AVCodecContext* codecCtx;
        private AVFrame* frame;
        private AVPacket* pkt;
        private SwsContext* sws;
        private int videoStreamIndex = -1;

        private Thread readerThread;
        private bool run;
        private byte[] rgbaBuffer;
        private GCHandle rgbaHandle;
        private IntPtr rgbaPtr;

        private double frameRate;
        private DateTime startTime;
        
        // Recording implementation
        private string recordingFilename;
        private List<FrameTime> frameTimes;
        private bool recordNextFrameTime;
        private bool manualRecording;
        private bool finalising;
        private DateTime recordingStartTime;
        private long frameCount;
        
        // Native hardware-accelerated recording using libavformat/libavcodec
        private AVFormatContext* outputFmt;
        private AVCodecContext* encoderCtx;
        private AVStream* videoStream;
        private AVFrame* encoderFrame;
        private AVPacket* encoderPkt;
        private SwsContext* encoderSws;
        private bool nativeRecordingEnabled;
        private long recordingPts;
        private bool headerWritten;
        private bool encoderReady;
        
        private FfmpegMediaFramework ffmpegMediaFramework;

        public override int FrameWidth => VideoConfig.VideoMode?.Width > 0 ? VideoConfig.VideoMode.Width : 640;
        public override int FrameHeight => VideoConfig.VideoMode?.Height > 0 ? VideoConfig.VideoMode.Height : 480;
        public override SurfaceFormat FrameFormat => SurfaceFormat.Color;

        // ICaptureFrameSource implementation
        public new bool IsVisible { get; set; }
        public FrameTime[] FrameTimes 
        {
            get
            {
                return frameTimes?.ToArray() ?? new FrameTime[0];
            }
        }
        public string Filename => recordingFilename;
        public new bool Recording { get; private set; }
        public bool RecordNextFrameTime 
        { 
            set { recordNextFrameTime = value; } 
        }
        public bool ManualRecording 
        { 
            get => manualRecording; 
            set => manualRecording = value; 
        }
        public bool Finalising => finalising;

        public FfmpegLibAvFoundationFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig) : base(videoConfig)
        {
            this.ffmpegMediaFramework = ffmpegMediaFramework;
            SurfaceFormat = SurfaceFormat.Color; // RGBA

            // Initialize recording fields
            frameTimes = new List<FrameTime>();
            recordingFilename = null;
            recordNextFrameTime = false;
            manualRecording = false;
            finalising = false;
            recordingStartTime = DateTime.MinValue;
            frameCount = 0;
            
            // Initialize native recording fields
            outputFmt = null;
            encoderCtx = null;
            videoStream = null;
            encoderFrame = null;
            encoderPkt = null;
            encoderSws = null;
            nativeRecordingEnabled = true; // Use native recording by default
            recordingPts = 0;

            // Check if native libraries are available before proceeding
            try
            {
                Console.WriteLine("FfmpegLibAvFoundationFrameSource: Starting explicit FFmpeg initialization...");
                
                // First ensure the global initializer is called
                if (!FfmpegGlobalInitializer.IsInitialized)
                {
                    Console.WriteLine("FfmpegLibAvFoundationFrameSource: Calling FfmpegGlobalInitializer.Initialize()...");
                    FfmpegGlobalInitializer.Initialize();
                }
                
                Console.WriteLine("FfmpegLibAvFoundationFrameSource: Ensuring FFmpeg registration...");
                FfmpegNativeLoader.EnsureRegistered();
                
                Console.WriteLine("FfmpegLibAvFoundationFrameSource: Testing FFmpeg function availability...");
                if (ffmpeg.av_log_set_level == null)
                {
                    throw new NotSupportedException("FFmpeg native libraries not properly loaded - av_log_set_level is null");
                }
                
                // Test function availability more defensively
                try
                {
                    Console.WriteLine("FfmpegLibAvFoundationFrameSource: Testing basic FFmpeg functions...");
                    
                    // Test version function first
                    try
                    {
                        var testVersion = ffmpeg.av_version_info();
                        Console.WriteLine($"FfmpegLibAvFoundationFrameSource: av_version_info works, version: {testVersion}");
                    }
                    catch (Exception versionEx)
                    {
                        Console.WriteLine($"FfmpegLibAvFoundationFrameSource: av_version_info failed: {versionEx.Message}");
                        throw new NotSupportedException($"Basic FFmpeg version function failed: {versionEx.Message}", versionEx);
                    }
                    
                    // Test allocation functions
                    try
                    {
                        var testFrame = ffmpeg.av_frame_alloc();
                        if (testFrame != null)
                        {
                            ffmpeg.av_frame_free(&testFrame);
                            Console.WriteLine("FfmpegLibAvFoundationFrameSource: Memory allocation functions work");
                        }
                    }
                    catch (Exception allocEx)
                    {
                        Console.WriteLine($"FfmpegLibAvFoundationFrameSource: Memory allocation functions failed: {allocEx.Message}");
                        // Don't fail here, just log
                    }
                    
                    Console.WriteLine("FfmpegLibAvFoundationFrameSource: FFmpeg basic functions test completed");
                }
                catch (Exception testEx)
                {
                    throw new NotSupportedException($"FFmpeg library test failed: {testEx.Message}", testEx);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FfmpegLibAvFoundationFrameSource: Native library initialization failed: {ex.GetType().Name}: {ex.Message}");
                Tools.Logger.VideoLog.LogCall(this, $"Native library initialization failed: {ex.Message}");
                throw new NotSupportedException($"FFmpeg native libraries not available: {ex.Message}", ex);
            }

            IsVisible = true;
        }

        public override IEnumerable<Mode> GetModes()
        {
            Tools.Logger.VideoLog.LogCall(this, $"GetModes() called (NATIVE) - detecting camera capabilities for '{VideoConfig.DeviceName}'");
            
            List<Mode> supportedModes = new List<Mode>();
            
            try
            {
                // Use native FFmpeg libraries to detect camera capabilities
                FfmpegNativeLoader.EnsureRegistered();
                
                // Get input format based on platform
                AVInputFormat* inputFormat = GetPlatformInputFormat();
                if (inputFormat == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Platform input format not available, using defaults");
                    return GetDefaultModes();
                }
                
                // Get device URL for this platform
                string deviceUrl = GetDeviceUrl();
                
                // Test common resolutions that cameras typically support
                var testResolutions = GetTestResolutions();
                
                Tools.Logger.VideoLog.LogCall(this, $"Testing {testResolutions.Length} resolution modes for device: {deviceUrl}");
                
                foreach (var testRes in testResolutions)
                {
                    if (TryTestResolution(inputFormat, deviceUrl, testRes.Width, testRes.Height, testRes.FrameRate))
                    {
                        supportedModes.Add(new Mode
                        {
                            Width = testRes.Width,
                            Height = testRes.Height,
                            FrameRate = testRes.FrameRate,
                            FrameWork = FrameWork.ffmpeg,
                            Index = 0,
                            Format = GetPlatformPixelFormat()
                        });
                        
                        Tools.Logger.VideoLog.LogCall(this, $"✓ Detected supported mode: {testRes.Width}x{testRes.Height}@{testRes.FrameRate}fps");
                    }
                    else
                    {
                        Tools.Logger.VideoLog.LogCall(this, $"✗ Mode not supported: {testRes.Width}x{testRes.Height}@{testRes.FrameRate}fps");
                    }
                }
                
                Tools.Logger.VideoLog.LogCall(this, $"Native camera capability detection complete: {supportedModes.Count} supported modes found");
                
                // If no modes detected, return defaults
                if (supportedModes.Count == 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, "No modes detected, returning defaults");
                    return GetDefaultModes();
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException(this, ex);
                Tools.Logger.VideoLog.LogCall(this, "Native mode detection failed, returning defaults");
                return GetDefaultModes();
            }
            
            return supportedModes;
        }
        
        private unsafe AVInputFormat* GetPlatformInputFormat()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return ffmpeg.av_find_input_format("avfoundation");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ffmpeg.av_find_input_format("dshow");
            }
            else
            {
                // Linux could use v4l2
                return ffmpeg.av_find_input_format("v4l2");
            }
        }
        
        private string GetDeviceUrl()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Use device index for AVFoundation
                return "0";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use device name for DirectShow
                return $"video=\"{VideoConfig.ffmpegId}\"";
            }
            else
            {
                // Linux v4l2
                return "/dev/video0";
            }
        }
        
        private string GetPlatformPixelFormat()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "uyvy422";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "rgb24";
            }
            else
            {
                return "yuyv422";
            }
        }
        
        private (int Width, int Height, int FrameRate)[] GetTestResolutions()
        {
            // Test resolutions in order of likelihood, starting with most common
            return new[]
            {
                (640, 480, 30),     // VGA - almost all cameras support this
                (1280, 720, 30),    // 720p
                (1920, 1080, 30),   // 1080p
                (1280, 720, 60),    // 720p 60fps
                (800, 600, 30),     // SVGA
                (1024, 768, 30),    // XGA
                (1552, 1552, 30),   // Square format (current mode)
                (1920, 1080, 60),   // 1080p 60fps
                (3840, 2160, 30),   // 4K (if supported)
            };
        }
        
        private IEnumerable<Mode> GetDefaultModes()
        {
            return new[]
            {
                new Mode { Width = 640, Height = 480, FrameRate = 30, FrameWork = FrameWork.ffmpeg, Format = "uyvy422" },
                new Mode { Width = 1280, Height = 720, FrameRate = 30, FrameWork = FrameWork.ffmpeg, Format = "uyvy422" },
                new Mode { Width = 1920, Height = 1080, FrameRate = 30, FrameWork = FrameWork.ffmpeg, Format = "uyvy422" }
            };
        }
        
        private unsafe bool TryTestResolution(AVInputFormat* inputFormat, string deviceUrl, int width, int height, int frameRate)
        {
            AVFormatContext* testFmt = null;
            AVDictionary* testOptions = null;
            
            try
            {
                // Set up test options for cross-platform compatibility
                string videoSize = $"{width}x{height}";
                ffmpeg.av_dict_set(&testOptions, "video_size", videoSize, 0);
                ffmpeg.av_dict_set(&testOptions, "framerate", frameRate.ToString(), 0);
                
                // Set platform-appropriate pixel format
                string pixelFormat = GetPlatformPixelFormat();
                ffmpeg.av_dict_set(&testOptions, "pixel_format", pixelFormat, 0);
                
                // Ultra-fast test - minimal timeouts to avoid hanging
                ffmpeg.av_dict_set(&testOptions, "probesize", "50000", 0);       // 50KB probe
                ffmpeg.av_dict_set(&testOptions, "analyzeduration", "200000", 0); // 0.2 seconds
                ffmpeg.av_dict_set(&testOptions, "fflags", "nobuffer", 0);
                
                // Platform-specific optimizations
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    ffmpeg.av_dict_set(&testOptions, "rtbufsize", "100M", 0);
                    ffmpeg.av_dict_set(&testOptions, "video_device_number", "0", 0);
                }
                
                // Try to open with this resolution
                int result = ffmpeg.avformat_open_input(&testFmt, deviceUrl, inputFormat, &testOptions);
                if (result >= 0)
                {
                    // Quick stream info check with timeout
                    if (ffmpeg.avformat_find_stream_info(testFmt, null) >= 0)
                    {
                        // Check if we found a video stream with approximately the requested dimensions
                        for (int i = 0; i < (int)testFmt->nb_streams; i++)
                        {
                            var stream = testFmt->streams[i];
                            if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                            {
                                int actualWidth = stream->codecpar->width;
                                int actualHeight = stream->codecpar->height;
                                
                                // Allow reasonable tolerance in dimensions (cameras might negotiate close resolutions)
                                int widthTolerance = Math.Max(50, width / 20);  // 5% or 50px, whichever is larger
                                int heightTolerance = Math.Max(50, height / 20);
                                
                                if (Math.Abs(actualWidth - width) <= widthTolerance && 
                                    Math.Abs(actualHeight - height) <= heightTolerance)
                                {
                                    Tools.Logger.VideoLog.LogCall(this, 
                                        $"Resolution test success: requested {width}x{height}, got {actualWidth}x{actualHeight}");
                                    return true;
                                }
                                else
                                {
                                    Tools.Logger.VideoLog.LogCall(this, 
                                        $"Resolution mismatch: requested {width}x{height}, got {actualWidth}x{actualHeight}");
                                }
                            }
                        }
                    }
                    else
                    {
                        Tools.Logger.VideoLog.LogCall(this, "Stream info detection failed");
                    }
                }
                else
                {
                    // Log the specific error for debugging
                    var error = GetFFmpegError(result);
                    Tools.Logger.VideoLog.LogCall(this, $"Failed to open device for {width}x{height}@{frameRate}: {error}");
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Exception testing {width}x{height}@{frameRate}: {ex.Message}");
                return false;
            }
            finally
            {
                if (testFmt != null)
                {
                    ffmpeg.avformat_close_input(&testFmt);
                }
                if (testOptions != null)
                {
                    ffmpeg.av_dict_free(&testOptions);
                }
            }
        }

        public override bool Start()
        {
            Tools.Logger.VideoLog.LogCall(this, "CAMERA ENGINE: ffmpeg LIB (in-process libav) - AVFoundation");
            
            // Ensure native dylibs are resolved
            FfmpegNativeLoader.EnsureRegistered();
            
            // Check if critical functions are available
            if (ffmpeg.avformat_open_input == null || ffmpeg.avformat_find_stream_info == null || 
                ffmpeg.avcodec_find_decoder == null || ffmpeg.avcodec_alloc_context3 == null)
            {
                throw new NotSupportedException("Critical FFmpeg functions not available - native libraries may be incompatible");
            }
            
            // Log FFmpeg version and configuration info
            Tools.Logger.VideoLog.LogCall(this, $"FFmpeg version: {ffmpeg.av_version_info()}");
            Tools.Logger.VideoLog.LogCall(this, $"libavformat version: {ffmpeg.avformat_version()}");
            Tools.Logger.VideoLog.LogCall(this, $"libavcodec version: {ffmpeg.avcodec_version()}");
            
            // Get configuration info
            try
            {
                string configStr = ffmpeg.avformat_configuration();
                Tools.Logger.VideoLog.LogCall(this, $"FFmpeg configuration: {configStr}");
                
                if (configStr.Contains("--enable-avfoundation") || configStr.Contains("--enable-indev=avfoundation"))
                {
                    Tools.Logger.VideoLog.LogCall(this, "✓ AVFoundation support enabled in FFmpeg build");
                }
                else
                {
                    Tools.Logger.VideoLog.LogCall(this, "⚠ AVFoundation support NOT enabled in FFmpeg build");
                    Tools.Logger.VideoLog.LogCall(this, $"Configuration string: {configStr}");
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Could not get FFmpeg configuration: {ex.Message}");
            }
            
            // Initialize FFmpeg and register all formats
            Tools.Logger.VideoLog.LogCall(this, "Initializing FFmpeg formats and devices...");
            
            // Try multiple approaches to register AVFoundation device format
            try
            {
                // Method 1: Try to register all devices
                ffmpeg.avdevice_register_all();
                Tools.Logger.VideoLog.LogCall(this, "✓ All devices registered successfully");
            }
            catch (Exception devEx)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Method 1 - Device registration failed: {devEx.Message}");
                
                // Method 2: Try alternative initialization approaches
                try
                {
                    // Force initialize format context - this sometimes triggers device registration
                    Tools.Logger.VideoLog.LogCall(this, "Trying alternative device initialization...");
                    
                    // Try to trigger format registration by attempting to allocate a format context
                    var testCtx = ffmpeg.avformat_alloc_context();
                    if (testCtx != null)
                    {
                        ffmpeg.avformat_free_context(testCtx);
                        Tools.Logger.VideoLog.LogCall(this, "✓ Format context allocation successful");
                    }
                }
                catch (Exception altEx)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Method 2 - Alternative initialization failed: {altEx.Message}");
                }
            }
            
            // Check what input formats are available
            Tools.Logger.VideoLog.LogCall(this, "Checking for AVFoundation format availability...");
            bool avfoundationFound = false;
            
            // Try multiple ways to find AVFoundation format
            AVInputFormat* avfFormat = null;
            
            // Method 1: Direct lookup
            avfFormat = ffmpeg.av_find_input_format("avfoundation");
            if (avfFormat != null)
            {
                avfoundationFound = true;
                Tools.Logger.VideoLog.LogCall(this, "✓ AVFoundation format found via direct lookup");
            }
            else
            {
                // Method 2: Try different name variations
                string[] formatNames = { "avfoundation", "AVFoundation", "qtkit", "QTKit" };
                foreach (var name in formatNames)
                {
                    avfFormat = ffmpeg.av_find_input_format(name);
                    if (avfFormat != null)
                    {
                        avfoundationFound = true;
                        Tools.Logger.VideoLog.LogCall(this, $"✓ AVFoundation format found as: {name}");
                        break;
                    }
                }
            }
            
            // Check what URL protocols are available
            Tools.Logger.VideoLog.LogCall(this, "Checking available URL protocols...");
            void* protocolOpaque = null;
            string protocolName;
            var protocolNames = new List<string>();
            while ((protocolName = ffmpeg.avio_enum_protocols(&protocolOpaque, 0)) != null)
            {
                protocolNames.Add(protocolName);
                if (protocolName.Contains("avfoundation") || protocolName.Contains("foundation"))
                {
                    Tools.Logger.VideoLog.LogCall(this, $"  ★ FOUND PROTOCOL: {protocolName}");
                }
            }
            Tools.Logger.VideoLog.LogCall(this, $"Available protocols (first 10): {string.Join(", ", protocolNames.Take(10))}");
            
            // Method 3: Try to force device format availability by attempting to open
            if (!avfoundationFound)
            {
                Tools.Logger.VideoLog.LogCall(this, "Attempting to force AVFoundation availability...");
                try
                {
                    // Try to open a format context with AVFoundation directly
                    AVFormatContext* testFmt = null;
                    AVDictionary* testOptions = null;
                    
                    // This might trigger registration of the format
                    int result = ffmpeg.avformat_open_input(&testFmt, "avfoundation:0", null, &testOptions);
                    if (testFmt != null)
                    {
                        ffmpeg.avformat_close_input(&testFmt);
                        Tools.Logger.VideoLog.LogCall(this, "✓ AVFoundation successfully opened directly - format should now be available");
                        
                        // Try the lookup again
                        avfFormat = ffmpeg.av_find_input_format("avfoundation");
                        if (avfFormat != null)
                        {
                            avfoundationFound = true;
                            Tools.Logger.VideoLog.LogCall(this, "✓ AVFoundation format now available after direct open");
                        }
                    }
                }
                catch (Exception directEx)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Direct AVFoundation open failed: {directEx.Message}");
                }
            }
            else
            {
                Tools.Logger.VideoLog.LogCall(this, "⚠ AVFoundation format not found");
                
                // List ALL available input formats for debugging - search for avfoundation
                Tools.Logger.VideoLog.LogCall(this, "Searching for AVFoundation in available input formats:");
                void* opaque = null;
                AVInputFormat* fmt_check = null;
                var formatCount = 0;
                var foundFormats = new List<string>();
                while ((fmt_check = ffmpeg.av_demuxer_iterate(&opaque)) != null && formatCount < 200)
                {
                    string formatName = Marshal.PtrToStringAnsi((IntPtr)fmt_check->name);
                    foundFormats.Add(formatName);
                    
                    // Check for any format that might be AVFoundation-related
                    if (formatName.Contains("avfoundation") || formatName.Contains("foundation") || 
                        formatName.Contains("qtkit") || formatName.Contains("videotoolbox") ||
                        formatName.Contains("coremedia") || formatName.Contains("avf"))
                    {
                        Tools.Logger.VideoLog.LogCall(this, $"  ★ FOUND POTENTIAL MATCH: {formatName}");
                    }
                    formatCount++;
                }
                Tools.Logger.VideoLog.LogCall(this, $"Searched {formatCount} formats, here are the first 15:");
                for (int i = 0; i < Math.Min(15, foundFormats.Count); i++)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"  - {foundFormats[i]}");
                }
            }
            
            if (!avfoundationFound)
            {
                Tools.Logger.VideoLog.LogCall(this, "AVFoundation format lookup failed, trying direct access approach...");
                
                // Since the format lookup fails but AVFoundation is compiled in, try direct approach
                // like how file playback works - directly open without format registration
                Tools.Logger.VideoLog.LogCall(this, "Attempting direct AVFoundation access without format registration...");
                
                try
                {
                    // Try to use AVFoundation directly like file formats, bypassing the broken registration
                    if (TryDirectAVFoundationAccess())
                    {
                        Tools.Logger.VideoLog.LogCall(this, "✓ Direct AVFoundation access successful!");
                        avfoundationFound = true;
                    }
                    else
                    {
                        Tools.Logger.VideoLog.LogCall(this, "Direct AVFoundation access failed");
                    }
                }
                catch (Exception directEx)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Direct AVFoundation access exception: {directEx.Message}");
                }
            }
            
            if (!avfoundationFound)
            {
                Tools.Logger.VideoLog.LogCall(this, "⚠ All AVFoundation access methods failed");
                Tools.Logger.VideoLog.LogCall(this, "FALLBACK: Using external FFmpeg process instead");
                throw new NotSupportedException("AVFoundation not supported in current FFmpeg build. Use external FFmpeg process instead.");
            }
            
            // Skip setting log level due to compatibility issues with FFmpeg.AutoGen
            Tools.Logger.VideoLog.LogCall(this, "Skipping av_log_set_level due to known compatibility issues");

            // Build AVFoundation device URL - try different formats
            string deviceName = VideoConfig.ffmpegId;
            
            // Since AVFoundation protocol is not available, try device index directly
            string deviceUrl;
            if (deviceName == "MacBook Pro Camera" || deviceName.Contains("Camera"))
            {
                deviceUrl = "0"; // Use index directly without protocol prefix
                Tools.Logger.VideoLog.LogCall(this, $"Mapping '{deviceName}' to device index 0 (no protocol prefix)");
            }
            else
            {
                deviceUrl = deviceName; // Use device name directly
                Tools.Logger.VideoLog.LogCall(this, $"Using device name directly: {deviceName}");
            }
            
            Tools.Logger.VideoLog.LogCall(this, $"Opening AVFoundation device: {deviceUrl}");

            // Set up input format and options
            // Use the AVFoundation format we successfully found earlier
            AVInputFormat* inputFormat = avfFormat; // Use the format we found
            
            Tools.Logger.VideoLog.LogCall(this, "Using AVFoundation format directly (format found, protocol not needed)");
            
            // If we couldn't verify direct access during startup, this will fail gracefully
            if (false) // Disable the old format check
            {
                // List available input formats for debugging
                Tools.Logger.VideoLog.LogCall(this, "Available input formats (first 20):");
                void* opaque_iter = null;
                AVInputFormat* fmt_iter = null;
                var iterCount = 0;
                while ((fmt_iter = ffmpeg.av_demuxer_iterate(&opaque_iter)) != null && iterCount < 20)
                {
                    string name = Marshal.PtrToStringAnsi((IntPtr)fmt_iter->name);
                    Tools.Logger.VideoLog.LogCall(this, $"  - {name}");
                    if (name.Contains("avfoundation") || name.Contains("qtkit") || name.Contains("foundation"))
                    {
                        Tools.Logger.VideoLog.LogCall(this, $"    ^ Found potential AVFoundation format: {name}");
                    }
                    iterCount++;
                }
                
                throw new NotSupportedException("AVFoundation input format not found in FFmpeg build. This FFmpeg installation may not include AVFoundation support.");
            }

            // Create format options dictionary
            AVDictionary* options = null;
            
            // Set video size
            string videoSize = $"{VideoConfig.VideoMode.Width}x{VideoConfig.VideoMode.Height}";
            ffmpeg.av_dict_set(&options, "video_size", videoSize, 0);
            
            // Set pixel format (equivalent to -pixel_format uyvy422)
            string pixelFormat = VideoConfig.VideoMode?.Format ?? "uyvy422";
            ffmpeg.av_dict_set(&options, "pixel_format", pixelFormat, 0);
            
            // Set frame rate
            if (VideoConfig.VideoMode?.FrameRate > 0)
            {
                string frameRateStr = VideoConfig.VideoMode.FrameRate.ToString("F0");
                ffmpeg.av_dict_set(&options, "framerate", frameRateStr, 0);
            }
            
            // AVFoundation-specific options optimized for drone racing real-time performance
            ffmpeg.av_dict_set(&options, "probesize", "1000000", 0); // Smaller probe for faster startup
            ffmpeg.av_dict_set(&options, "analyzeduration", "1000000", 0); // 1 second analysis for faster init
            
            // Ultra low-delay flags for drone racing
            ffmpeg.av_dict_set(&options, "fflags", "nobuffer+flush_packets", 0);
            ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);
            ffmpeg.av_dict_set(&options, "flush_packets", "1", 0);
            ffmpeg.av_dict_set(&options, "max_delay", "0", 0);

            Tools.Logger.VideoLog.LogCall(this, $"Opening input with options: video_size={videoSize}, pixel_format={pixelFormat}");

            fixed (AVFormatContext** pfmt = &fmt)
            {
                int result = ffmpeg.avformat_open_input(pfmt, deviceUrl, inputFormat, &options);
                if (result < 0)
                {
                    var error = GetFFmpegError(result);
                    throw new Exception($"avformat_open_input failed for device '{deviceName}': {error}");
                }
            }
            
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) 
                throw new Exception("avformat_find_stream_info failed");

            // Find video stream
            for (int i = 0; i < (int)fmt->nb_streams; i++)
            {
                if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIndex = i; 
                    break;
                }
            }
            if (videoStreamIndex < 0) throw new Exception("no video stream found");

            var st = fmt->streams[videoStreamIndex];
            var codecpar = st->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
            if (codec == null) throw new Exception("decoder not found");
            
            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (codecCtx == null) throw new Exception("alloc codec ctx failed");
            
            if (ffmpeg.avcodec_parameters_to_context(codecCtx, codecpar) < 0) 
                throw new Exception("params->ctx failed");
            
            codecCtx->thread_count = 0; // auto threads
            codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
            
            if (ffmpeg.avcodec_open2(codecCtx, codec, null) < 0) 
                throw new Exception("avcodec_open2 failed");

            // Update VideoConfig with actual dimensions
            VideoConfig.VideoMode ??= new Mode { Width = codecCtx->width, Height = codecCtx->height, FrameRate = 30, FrameWork = FrameWork.ffmpeg };
            VideoConfig.VideoMode.Width = codecCtx->width;
            VideoConfig.VideoMode.Height = codecCtx->height;

            // Calculate frame rate
            if (st->avg_frame_rate.den != 0)
            {
                frameRate = st->avg_frame_rate.num / (double)st->avg_frame_rate.den;
            }
            else if (st->r_frame_rate.den != 0)
            {
                frameRate = st->r_frame_rate.num / (double)st->r_frame_rate.den;
            }
            if (frameRate <= 0) frameRate = 30.0;

            // Allocate IO
            frame = ffmpeg.av_frame_alloc();
            pkt = ffmpeg.av_packet_alloc();
            sws = null;

            int bufSize = FrameWidth * FrameHeight * 4;
            rgbaBuffer = new byte[bufSize];
            rgbaHandle = GCHandle.Alloc(rgbaBuffer, GCHandleType.Pinned);
            rgbaPtr = rgbaHandle.AddrOfPinnedObject();
            rawTextures = new XBuffer<RawTexture>(5, FrameWidth, FrameHeight);

            run = true;
            readerThread = new Thread(ReadLoop) { Name = "libav-avfoundation" };
            readerThread.Start();

            startTime = DateTime.UtcNow;
            Connected = true;
            
            Tools.Logger.VideoLog.LogCall(this, $"Native AVFoundation camera started: {FrameWidth}x{FrameHeight}@{frameRate:F1}fps");
            
            return base.Start();
        }

        private void EnsureSws()
        {
            if (sws != null) return;
            sws = ffmpeg.sws_getContext(codecCtx->width, codecCtx->height, codecCtx->pix_fmt,
                                        codecCtx->width, codecCtx->height, AVPixelFormat.AV_PIX_FMT_RGBA,
                                        ffmpeg.SWS_BILINEAR, null, null, null);
            if (sws == null) throw new Exception("sws_getContext failed");
        }

        private void ReadLoop()
        {
            Tools.Logger.VideoLog.LogCall(this, "Native AVFoundation ReadLoop: Starting camera capture thread");
            
            // Wait for proper initialization
            int maxWaitAttempts = 50;
            while ((rawTextures == null || rgbaPtr == IntPtr.Zero) && maxWaitAttempts > 0 && run)
            {
                Thread.Sleep(10);
                maxWaitAttempts--;
            }
            
            if (!run || rawTextures == null || rgbaPtr == IntPtr.Zero)
            {
                Tools.Logger.VideoLog.LogCall(this, "ReadLoop: Failed to initialize - exiting");
                return;
            }
            
            Tools.Logger.VideoLog.LogCall(this, "Native AVFoundation ReadLoop: Initialization complete, starting capture loop");
            
            int retryCount = 0;
            while (run)
            {
                try
                {
                    int readResult = ffmpeg.av_read_frame(fmt, pkt);
                    if (readResult < 0)
                    {
                        // Get detailed error information
                        var error = GetFFmpegError(readResult);
                        Tools.Logger.VideoLog.LogCall(this, $"Camera read error (code: {readResult}): {error}");
                        
                        // Check if it's EOF or a real error
                        if (readResult == ffmpeg.AVERROR_EOF)
                        {
                            Tools.Logger.VideoLog.LogCall(this, "Camera stream ended (EOF)");
                        }
                        else if (readResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        {
                            // For drone racing, minimize delay when frames aren't immediately available
                            retryCount++;
                            
                            // During initial startup, allow longer delays
                            if (retryCount < 100)
                            {
                                if (retryCount % 30 == 0) // Log every 30 attempts to reduce noise
                                {
                                    Tools.Logger.VideoLog.LogCall(this, $"Camera initializing... (attempt {retryCount})");
                                }
                                Thread.Sleep(10); // Shorter sleep during startup
                            }
                            else
                            {
                                // After startup, use minimal delay for smooth real-time performance
                                if (retryCount % 300 == 0) // Log every 300 attempts to reduce spam
                                {
                                    Tools.Logger.VideoLog.LogCall(this, $"Camera frame not ready (attempt {retryCount}) - using minimal delay for real-time performance");
                                }
                                Thread.Sleep(1); // Ultra-minimal delay for drone racing
                            }
                            
                            // Give up after 5 minutes but with faster attempts
                            if (retryCount > 30000) // 30000 * 1ms = 30 seconds max
                            {
                                Tools.Logger.VideoLog.LogCall(this, "Camera failed to provide frames after extended period - giving up");
                                Connected = false;
                                break;
                            }
                            
                            continue;
                        }
                        else
                        {
                            Tools.Logger.VideoLog.LogCall(this, "Camera disconnected or fatal read error");
                            Connected = false;
                            break;
                        }
                    }
                    
                    if (pkt->stream_index != videoStreamIndex)
                    {
                        ffmpeg.av_packet_unref(pkt);
                        continue;
                    }
                    
                    // Log successful frame reception if we were retrying (reduced logging for performance)
                    if (retryCount > 0)
                    {
                        // Only log every 100 successful recoveries to reduce log spam
                        if (retryCount > 10)
                        {
                            Tools.Logger.VideoLog.LogCall(this, $"✓ Camera frame received after {retryCount} retries!");
                        }
                        retryCount = 0; // Reset retry count
                    }

                    // Send packet to decoder
                    int sendResult = ffmpeg.avcodec_send_packet(codecCtx, pkt);
                    ffmpeg.av_packet_unref(pkt);
                    
                    if (sendResult < 0 && sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        continue;
                    }
                    
                    // Try to receive frames
                    int receiveResult;
                    while ((receiveResult = ffmpeg.avcodec_receive_frame(codecCtx, frame)) == 0)
                    {
                        if (run)
                        {
                            ProcessCurrentFrame(frame);
                        }
                    }
                    
                    // Small sleep to prevent CPU spinning
                    Thread.Sleep(1);
                }
                catch (Exception ex)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"ReadLoop error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
            
            Tools.Logger.VideoLog.LogCall(this, "Native AVFoundation ReadLoop: Camera capture thread finished");
        }
        
        private void ProcessCurrentFrame(AVFrame* frame)
        {
            try
            {
                EnsureSws();
                byte_ptrArray8 srcData = frame->data;
                int_array8 srcLinesize = frame->linesize;
                
                byte_ptrArray4 tmpData = default;
                int_array4 tmpLines = default;
                int imgAlloc = ffmpeg.av_image_alloc(ref tmpData, ref tmpLines, codecCtx->width, codecCtx->height, AVPixelFormat.AV_PIX_FMT_RGBA, 1);
                if (imgAlloc >= 0)
                {
                    try
                    {
                        // Convert to RGBA
                        ffmpeg.sws_scale(sws, srcData, srcLinesize, 0, codecCtx->height, tmpData, tmpLines);
                        
                        // Copy directly into our pinned rgbaBuffer (no flipping needed for native AVFoundation)
                        int stride = codecCtx->width * 4;
                        byte* srcPtr = tmpData[0];
                        byte* dstPtr = (byte*)rgbaPtr.ToPointer();
                        
                        // Direct copy without flipping - native AVFoundation provides correct orientation
                        Buffer.MemoryCopy(srcPtr, dstPtr, rgbaBuffer.Length, codecCtx->height * stride);
                        
                        // Handle native recording
                        if (Recording && nativeRecordingEnabled)
                        {
                            WriteFrameToRecording(dstPtr);
                        }
                        else if (Recording && !nativeRecordingEnabled)
                        {
                            if (recordingPts % 60 == 0) // Log occasionally
                            {
                                Tools.Logger.VideoLog.LogCall(this, "ProcessCurrentFrame: Recording=true but nativeRecordingEnabled=false - skipping frame");
                            }
                        }
                        
                        // Handle frame timing for recording
                        if (recordNextFrameTime)
                        {
                            DateTime frameTime = UnifiedFrameTimingManager.GetHighPrecisionTimestamp();
                            
                            if (recordingStartTime == DateTime.MinValue)
                            {
                                recordingStartTime = frameTime;
                            }
                            
                            var frameTimeEntry = UnifiedFrameTimingManager.CreateFrameTime(
                                (int)FrameProcessNumber, frameTime, recordingStartTime);
                            frameTimes.Add(frameTimeEntry);
                            
                            frameCount++;
                            recordNextFrameTime = false;
                        }
                        
                        // Push to RawTexture ring buffer
                        if (rawTextures.GetWritable(out RawTexture raw))
                        {
                            // Calculate SampleTime for live camera
                            var currentTime = DateTime.UtcNow;
                            var elapsed = currentTime - startTime;
                            SampleTime = (long)(elapsed.TotalSeconds * 10000000.0);
                            
                            raw.SetData(rgbaPtr, SampleTime, ++FrameProcessNumber);
                            rawTextures.WriteOne(raw);
                            NotifyReceivedFrame();
                        }
                    }
                    finally
                    {
                        ffmpeg.av_free((void*)tmpData[0]);
                    }
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"ProcessCurrentFrame error: {ex.Message}");
            }
        }

        public void StartRecording(string filename)
        {
            Tools.Logger.VideoLog.LogCall(this, $"StartRecording: ENTRY - filename={filename}, Recording={Recording}, nativeRecordingEnabled={nativeRecordingEnabled}");
            
            if (Recording)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Already recording to {recordingFilename}");
                return;
            }

            recordingFilename = filename;
            recordingStartTime = DateTime.MinValue;
            frameCount = 0;
            frameTimes.Clear();
            finalising = false;
            recordingPts = 0;
            headerWritten = false;
            encoderReady = false;
            Recording = false; // Initialize to false, will be set to true only after successful encoder init

            if (nativeRecordingEnabled)
            {
                Tools.Logger.VideoLog.LogCall(this, $"StartRecording: Attempting to initialize native recording to {filename}");
                // Use native hardware-accelerated recording
                bool initResult = InitializeNativeRecording(filename);
                Tools.Logger.VideoLog.LogCall(this, $"StartRecording: InitializeNativeRecording returned {initResult}");
                
                if (initResult)
                {
                    Recording = true; // Only set to true after successful initialization
                    Tools.Logger.VideoLog.LogCall(this, $"StartRecording: Successfully started native hardware-accelerated AVFoundation recording to {filename}");
                    Tools.Logger.VideoLog.LogCall(this, $"StartRecording: Recording state - Recording={Recording}, nativeRecordingEnabled={nativeRecordingEnabled}");
                    return;
                }
                else
                {
                    Tools.Logger.VideoLog.LogCall(this, "StartRecording: Failed to initialize native recording, keeping recording disabled");
                    Recording = false;
                    return;
                }
            }
            else
            {
                Tools.Logger.VideoLog.LogCall(this, $"StartRecording: Native recording disabled, not starting recording");
                Recording = false;
            }
        }

        public void StopRecording()
        {
            if (!Recording)
            {
                Tools.Logger.VideoLog.LogCall(this, "Not currently recording");
                return;
            }

            Tools.Logger.VideoLog.LogCall(this, $"Stopping native hardware-accelerated AVFoundation recording to {recordingFilename}");
            Recording = false;
            finalising = true;

            if (nativeRecordingEnabled)
            {
                FinalizeNativeRecording();
            }

            finalising = false;
            Tools.Logger.VideoLog.LogCall(this, $"Stopped native hardware-accelerated AVFoundation recording to {recordingFilename}");
        }

        public override bool Stop()
        {
            Tools.Logger.VideoLog.LogCall(this, "Native AVFoundation frame source stopping");
            
            run = false;
            Connected = false;
            
            // Stop the reader thread
            if (readerThread != null && readerThread.IsAlive)
            {
                Tools.Logger.VideoLog.LogCall(this, "Waiting for native reader thread to stop...");
                if (!readerThread.Join(2000))
                {
                    Tools.Logger.VideoLog.LogCall(this, "WARNING: Native reader thread did not stop within 2 seconds");
                }
                else
                {
                    Tools.Logger.VideoLog.LogCall(this, "Native reader thread stopped successfully");
                }
            }
            
            return base.Stop();
        }

        private unsafe bool InitializeNativeRecordingSoftware(string filename)
        {
            // Try multiple software encoders in order of preference
            string[] softwareEncoders = { "libx264", "h264", "libopenh264" };
            
            foreach (string encoder in softwareEncoders)
            {
                Tools.Logger.VideoLog.LogCall(this, $"InitializeNativeRecordingSoftware: Trying software encoder: {encoder}");
                if (InitializeNativeRecordingWithCodec(filename, encoder))
                {
                    Tools.Logger.VideoLog.LogCall(this, $"InitializeNativeRecordingSoftware: Successfully initialized with {encoder}");
                    return true;
                }
            }
            
            Tools.Logger.VideoLog.LogCall(this, "InitializeNativeRecordingSoftware: All software encoders failed");
            return false;
        }

        private unsafe bool InitializeNativeRecording(string filename)
        {
            // Try hardware first, then fallback to software
            Tools.Logger.VideoLog.LogCall(this, "InitializeNativeRecording: Attempting VideoToolbox hardware encoding");
            if (InitializeNativeRecordingWithCodec(filename, "h264_videotoolbox"))
            {
                Tools.Logger.VideoLog.LogCall(this, "InitializeNativeRecording: VideoToolbox hardware encoding successful");
                return true;
            }
            
            Tools.Logger.VideoLog.LogCall(this, "InitializeNativeRecording: VideoToolbox failed, trying alternative approaches");
            
            // If VideoToolbox failed due to container incompatibility, try MP4 container
            if (filename.EndsWith(".mkv"))
            {
                string mp4Filename = filename.Replace(".mkv", ".mp4");
                Tools.Logger.VideoLog.LogCall(this, $"InitializeNativeRecording: Trying VideoToolbox with MP4 container: {mp4Filename}");
                
                if (InitializeNativeRecordingWithCodec(mp4Filename, "h264_videotoolbox"))
                {
                    Tools.Logger.VideoLog.LogCall(this, "InitializeNativeRecording: VideoToolbox successful with MP4 container");
                    return true;
                }
            }
            
            Tools.Logger.VideoLog.LogCall(this, "InitializeNativeRecording: All VideoToolbox attempts failed, falling back to software encoding");
            return InitializeNativeRecordingSoftware(filename);
        }

        private unsafe bool InitializeNativeRecordingWithCodec(string filename, string codecName)
        {
            try
            {
                Tools.Logger.VideoLog.LogCall(this, $"InitializeNativeRecordingWithCodec: ENTRY - filename={filename}, codec={codecName}");
                
                // Ensure output directory exists
                string outputDir = Path.GetDirectoryName(filename);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Tools.Logger.VideoLog.LogCall(this, $"InitializeNativeRecordingWithCodec: Creating directory {outputDir}");
                    Directory.CreateDirectory(outputDir);
                }

                // Initialize output format context
                fixed (AVFormatContext** pfmt = &outputFmt)
                {
                    if (ffmpeg.avformat_alloc_output_context2(pfmt, null, null, filename) < 0)
                    {
                        Tools.Logger.VideoLog.LogCall(this, "Failed to allocate output format context");
                        return false;
                    }
                }

                // Find the specified encoder
                AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
                if (codec == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Encoder {codecName} not found");
                    
                    // List available H.264 encoders for debugging
                    Tools.Logger.VideoLog.LogCall(this, "Available H.264 encoders:");
                    AVCodec* availableCodec = null;
                    void* iter = null;
                    while ((availableCodec = ffmpeg.av_codec_iterate(&iter)) != null)
                    {
                        if (availableCodec->type == AVMediaType.AVMEDIA_TYPE_VIDEO && 
                            ffmpeg.av_codec_is_encoder(availableCodec) == 1 &&
                            availableCodec->id == AVCodecID.AV_CODEC_ID_H264)
                        {
                            string name = Marshal.PtrToStringAnsi((IntPtr)availableCodec->name);
                            Tools.Logger.VideoLog.LogCall(this, $"  - {name}");
                        }
                    }
                    
                    return false;
                }

                Tools.Logger.VideoLog.LogCall(this, $"Using video encoder: {codecName}");

                // Create video stream
                videoStream = ffmpeg.avformat_new_stream(outputFmt, codec);
                if (videoStream == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to create video stream");
                    return false;
                }

                // Allocate encoder context
                encoderCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (encoderCtx == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to allocate encoder context");
                    return false;
                }

                // Configure encoder - basic settings first
                encoderCtx->width = FrameWidth;
                encoderCtx->height = FrameHeight;
                
                // Use YUV420P for better compatibility across all encoders
                encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                Tools.Logger.VideoLog.LogCall(this, "Using YUV420P pixel format for maximum compatibility");
                
                // Configure for variable frame rate (VFR) to support timestamp-based recording
                // Use high-resolution time base for precise PTS timing
                encoderCtx->time_base = new AVRational { num = 1, den = 90000 }; // 90kHz time base (standard for H.264)
                encoderCtx->framerate = new AVRational { num = 0, den = 1 }; // 0 indicates variable frame rate
                
                Tools.Logger.VideoLog.LogCall(this, $"InitializeNativeRecording: Encoder config - {FrameWidth}x{FrameHeight} @ {frameRate:F1}fps, codec: {codecName}");
                
                // Hardware encoder specific settings
                if (codecName.Contains("videotoolbox"))
                {
                    // VideoToolbox settings - optimized for MKV compatibility
                    encoderCtx->bit_rate = 2000000; // 2 Mbps for better stability
                    encoderCtx->gop_size = 30; // Keyframe interval for seeking
                    encoderCtx->max_b_frames = 0; // Disable B-frames for MKV compatibility
                    Tools.Logger.VideoLog.LogCall(this, $"InitializeNativeRecording: Using VideoToolbox with bitrate {encoderCtx->bit_rate}, GOP {encoderCtx->gop_size}");
                    
                    // Set color space and range for VideoToolbox
                    encoderCtx->color_range = AVColorRange.AVCOL_RANGE_MPEG; // Use MPEG range
                    encoderCtx->colorspace = AVColorSpace.AVCOL_SPC_BT709;   // BT.709 color space
                    encoderCtx->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709; // BT.709 primaries
                    encoderCtx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709; // BT.709 transfer
                    
                    // Set VideoToolbox options for MKV compatibility
                    int ret1 = ffmpeg.av_opt_set(encoderCtx->priv_data, "allow_sw", "1", 0); // Allow software fallback
                    int ret2 = ffmpeg.av_opt_set(encoderCtx->priv_data, "profile", "main", 0); // Main profile for better MKV compatibility
                    int ret3 = ffmpeg.av_opt_set(encoderCtx->priv_data, "level", "3.1", 0); // H.264 level for compatibility
                    Tools.Logger.VideoLog.LogCall(this, $"InitializeNativeRecording: VideoToolbox MKV-compatible options - allow_sw: {ret1}, profile: {ret2}, level: {ret3}");
                }
                else
                {
                    // Software encoder settings
                    encoderCtx->bit_rate = 5000000;
                    int ret1 = ffmpeg.av_opt_set(encoderCtx->priv_data, "preset", "medium", 0);
                    int ret2 = ffmpeg.av_opt_set(encoderCtx->priv_data, "tune", "zerolatency", 0);
                    Tools.Logger.VideoLog.LogCall(this, $"InitializeNativeRecording: Software encoder options - preset: {ret1}, tune: {ret2}");
                }

                // Set GOP size for keyframes
                encoderCtx->gop_size = Math.Max(1, (int)(frameRate * 0.1f)); // Keyframe every 0.1s

                // Open encoder first
                int openResult = ffmpeg.avcodec_open2(encoderCtx, codec, null);
                if (openResult < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Failed to open {codecName} encoder: {GetFFmpegError(openResult)}");
                    
                    // If VideoToolbox failed, try software encoder as fallback
                    if (codecName.Contains("videotoolbox"))
                    {
                        Tools.Logger.VideoLog.LogCall(this, "VideoToolbox failed, falling back to software encoder (libx264)...");
                        
                        // Free the VideoToolbox encoder context
                        fixed (AVCodecContext** pEncoderCtx = &encoderCtx)
                        {
                            ffmpeg.avcodec_free_context(pEncoderCtx);
                        }
                        
                        // Find software encoder
                        codec = ffmpeg.avcodec_find_encoder_by_name("libx264");
                        if (codec == null)
                        {
                            Tools.Logger.VideoLog.LogCall(this, "Software encoder libx264 not found");
                            return false;
                        }
                        codecName = "libx264";
                        
                        // Allocate new encoder context for software encoder
                        encoderCtx = ffmpeg.avcodec_alloc_context3(codec);
                        if (encoderCtx == null)
                        {
                            Tools.Logger.VideoLog.LogCall(this, "Failed to allocate software encoder context");
                            return false;
                        }
                        
                        // Reconfigure for software encoder
                        encoderCtx->width = FrameWidth;
                        encoderCtx->height = FrameHeight;
                        encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                        // Configure software encoder for variable frame rate (VFR) to support timestamp-based recording
                        encoderCtx->time_base = new AVRational { num = 1, den = 90000 }; // 90kHz time base (standard for H.264)
                        encoderCtx->framerate = new AVRational { num = 0, den = 1 }; // 0 indicates variable frame rate
                        encoderCtx->bit_rate = 2000000;
                        encoderCtx->gop_size = Math.Max(1, (int)(frameRate * 0.1f));
                        
                        // Software encoder specific options
                        int ret1 = ffmpeg.av_opt_set(encoderCtx->priv_data, "preset", "fast", 0);
                        int ret2 = ffmpeg.av_opt_set(encoderCtx->priv_data, "tune", "zerolatency", 0);
                        Tools.Logger.VideoLog.LogCall(this, $"Software encoder options - preset: {ret1}, tune: {ret2}");
                        
                        // Try to open software encoder
                        openResult = ffmpeg.avcodec_open2(encoderCtx, codec, null);
                        if (openResult < 0)
                        {
                            Tools.Logger.VideoLog.LogCall(this, $"Software encoder also failed: {GetFFmpegError(openResult)}");
                            return false;
                        }
                        Tools.Logger.VideoLog.LogCall(this, "Software encoder (libx264) opened successfully");
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    Tools.Logger.VideoLog.LogCall(this, $"{codecName} encoder opened successfully");
                }

                // Copy parameters to stream AFTER encoder is opened
                if (ffmpeg.avcodec_parameters_from_context(videoStream->codecpar, encoderCtx) < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to copy encoder parameters to stream");
                    return false;
                }
                
                // Configure stream for variable frame rate (VFR) support in external players
                videoStream->time_base = encoderCtx->time_base; // Use same high-resolution time base
                videoStream->avg_frame_rate = new AVRational { num = 0, den = 1 }; // Indicate VFR to players
                videoStream->r_frame_rate = new AVRational { num = 0, den = 1 }; // Real frame rate is variable
                
                Tools.Logger.VideoLog.LogCall(this, "Stream configured for variable frame rate (VFR) - external players should use PTS timing");

                // Allocate frame and packet
                encoderFrame = ffmpeg.av_frame_alloc();
                encoderPkt = ffmpeg.av_packet_alloc();
                
                if (encoderFrame == null || encoderPkt == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to allocate encoder frame or packet");
                    return false;
                }

                // Configure frame with same format as encoder
                encoderFrame->format = (int)encoderCtx->pix_fmt;
                encoderFrame->width = FrameWidth;
                encoderFrame->height = FrameHeight;
                
                if (ffmpeg.av_frame_get_buffer(encoderFrame, 32) < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to allocate encoder frame buffer");
                    return false;
                }

                // Initialize SWS context for RGBA to encoder pixel format conversion
                AVPixelFormat targetFormat = encoderCtx->pix_fmt;
                Tools.Logger.VideoLog.LogCall(this, $"Initializing SWS context: RGBA -> {targetFormat}");
                encoderSws = ffmpeg.sws_getContext(
                    FrameWidth, FrameHeight, AVPixelFormat.AV_PIX_FMT_RGBA,
                    FrameWidth, FrameHeight, targetFormat,
                    ffmpeg.SWS_BILINEAR, null, null, null);
                
                if (encoderSws == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to initialize SWS context for recording");
                    return false;
                }

                // Open output file
                if ((outputFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    if (ffmpeg.avio_open(&outputFmt->pb, filename, ffmpeg.AVIO_FLAG_WRITE) < 0)
                    {
                        Tools.Logger.VideoLog.LogCall(this, $"Failed to open output file: {filename}");
                        return false;
                    }
                }

                // NOTE: We will write the header after the first frame is successfully encoded
                // This is required for VideoToolbox as it needs to process at least one frame
                // to determine the correct stream parameters
                Tools.Logger.VideoLog.LogCall(this, "Output file opened, header will be written after first frame");

                // Allow encoder to settle before processing frames
                System.Threading.Thread.Sleep(100); // 100ms delay
                
                // Mark encoder as ready
                encoderReady = true;
                Tools.Logger.VideoLog.LogCall(this, $"Native recording initialized successfully using {codecName} - encoder ready");
                return true;
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Exception initializing native recording: {ex.Message}");
                return false;
            }
        }

        private unsafe void WriteFrameToRecording(byte* rgbaData)
        {
            if (!Recording || encoderCtx == null || encoderFrame == null || !encoderReady)
            {
                if (!Recording) Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: Not recording");
                if (encoderCtx == null) Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: encoderCtx is null");
                if (encoderFrame == null) Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: encoderFrame is null");
                if (!encoderReady) Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: Encoder not ready yet");
                return;
            }

            try
            {
                // Log frame writing progress occasionally
                if (recordingPts % 30 == 0) // Every 30 frames (about 1 second at 30fps)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Writing frame {recordingPts}");
                }

                // Convert RGBA to YUV420P
                byte_ptrArray4 srcData = new byte_ptrArray4();
                int_array4 srcLinesize = new int_array4();
                srcData[0] = rgbaData;
                srcLinesize[0] = FrameWidth * 4; // RGBA stride

                int scaleResult = ffmpeg.sws_scale(encoderSws, srcData, srcLinesize, 0, FrameHeight,
                    encoderFrame->data, encoderFrame->linesize);
                if (scaleResult != FrameHeight)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: sws_scale returned {scaleResult}, expected {FrameHeight}");
                }

                // Calculate PTS based on actual timestamp instead of frame counter
                DateTime currentFrameTime = DateTime.Now;
                TimeSpan timeSinceRecordingStart = currentFrameTime - recordingStartTime;
                
                // Convert timestamp to encoder time_base units for proper PTS
                // encoder time_base is typically 1/framerate (e.g., 1/30 for 30fps)
                double timebaseSeconds = (double)encoderCtx->time_base.num / encoderCtx->time_base.den;
                long calculatedPts = (long)(timeSinceRecordingStart.TotalSeconds / timebaseSeconds);
                
                encoderFrame->pts = calculatedPts;
                
                // Log timing occasionally for debugging
                if (recordingPts % 30 == 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Frame {recordingPts} PTS={calculatedPts} (time: {timeSinceRecordingStart.TotalSeconds:F3}s, timebase: {timebaseSeconds:F6}s)");
                }
                recordingPts++; // Keep counter for logging purposes

                // Encode frame
                Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Sending frame {recordingPts} to encoder");
                int ret = ffmpeg.avcodec_send_frame(encoderCtx, encoderFrame);
                Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: avcodec_send_frame returned: {ret}");
                
                if (ret < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Error sending frame to encoder: {GetFFmpegError(ret)} (code: {ret})");
                    
                    // Check if this is a VideoToolbox encoding error (-12912)
                    if (ret == -12912 && recordingPts == 0) // Only try fallback on first frame
                    {
                        Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: VideoToolbox encoding failed, attempting software fallback...");
                        
                        // Stop current recording and restart with software encoder
                        Recording = false;
                        CleanupNativeRecording();
                        
                        // Reinitialize with software encoder forced
                        if (InitializeNativeRecordingSoftware(recordingFilename))
                        {
                            Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: Successfully switched to software encoder");
                            // Retry encoding with software encoder
                            ret = ffmpeg.avcodec_send_frame(encoderCtx, encoderFrame);
                            if (ret < 0)
                            {
                                Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Software encoder also failed: {GetFFmpegError(ret)}");
                                return;
                            }
                            Recording = true; // Re-enable recording
                            Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: Software encoder frame encoding succeeded");
                        }
                        else
                        {
                            Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: Software fallback initialization failed");
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }

                // Retrieve encoded packets first (VideoToolbox may need to flush before header writing)
                int packetsReceived = 0;
                while (ret >= 0)
                {
                    ret = ffmpeg.avcodec_receive_packet(encoderCtx, encoderPkt);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        break;
                    
                    if (ret < 0)
                    {
                        Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Error receiving packet from encoder: {GetFFmpegError(ret)}");
                        break;
                    }
                    
                    packetsReceived++;

                    // Write header after receiving first packet (when encoder parameters are fully finalized)
                    if (!headerWritten)
                    {
                        Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Received first packet ({packetsReceived}), attempting to write file header");
                        Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Encoder state - width: {encoderCtx->width}, height: {encoderCtx->height}, pix_fmt: {encoderCtx->pix_fmt}");
                        
                        // Update stream parameters after first packet (VideoToolbox finalizes parameters after first packet)
                        Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: Updating stream parameters after first packet");
                        int paramResult = ffmpeg.avcodec_parameters_from_context(videoStream->codecpar, encoderCtx);
                        if (paramResult < 0)
                        {
                            Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Failed to update stream parameters: {GetFFmpegError(paramResult)}");
                            ffmpeg.av_packet_unref(encoderPkt);
                            return;
                        }
                        Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: Stream parameters updated successfully");
                        
                        int headerResult = ffmpeg.avformat_write_header(outputFmt, null);
                        if (headerResult < 0)
                        {
                            Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Failed to write file header: {GetFFmpegError(headerResult)} (code: {headerResult})");
                            
                            // If VideoToolbox header writing fails, try MP4 container first, then software fallback
                            if (headerResult == -1094995529) // AVERROR_INVALIDDATA - VideoToolbox/MKV incompatibility
                            {
                                Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: VideoToolbox header incompatible with MKV container");
                                
                                // Stop current recording and try MP4 container with VideoToolbox
                                Recording = false;
                                CleanupNativeRecording();
                                
                                // Try MP4 container with same VideoToolbox encoder
                                if (recordingFilename.EndsWith(".mkv"))
                                {
                                    string mp4Filename = recordingFilename.Replace(".mkv", ".mp4");
                                    Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Trying VideoToolbox with MP4 container: {mp4Filename}");
                                    
                                    if (InitializeNativeRecordingWithCodec(mp4Filename, "h264_videotoolbox"))
                                    {
                                        Recording = true;
                                        recordingFilename = mp4Filename; // Update filename for MP4
                                        Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: Successfully switched to VideoToolbox + MP4 container");
                                        ffmpeg.av_packet_unref(encoderPkt);
                                        return; // Exit this frame, next frame will use MP4 container
                                    }
                                    else
                                    {
                                        Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: VideoToolbox + MP4 fallback also failed");
                                    }
                                }
                                
                                // If MP4 failed, try software encoder fallback
                                Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: Trying software encoder as final fallback");
                                if (InitializeNativeRecordingSoftware(recordingFilename))
                                {
                                    Recording = true;
                                    Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: Successfully switched to software encoder for recording");
                                    ffmpeg.av_packet_unref(encoderPkt);
                                    return; // Exit this frame, next frame will use software encoder
                                }
                                else
                                {
                                    Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: All fallback attempts failed");
                                }
                            }
                            
                            // Mark header as written to prevent infinite retries
                            headerWritten = true;
                            ffmpeg.av_packet_unref(encoderPkt);
                            return;
                        }
                        headerWritten = true;
                        Tools.Logger.VideoLog.LogCall(this, "WriteFrameToRecording: File header written successfully");
                    }

                    // Rescale packet timing
                    ffmpeg.av_packet_rescale_ts(encoderPkt, encoderCtx->time_base, videoStream->time_base);
                    encoderPkt->stream_index = videoStream->index;

                    // Write packet to file
                    ret = ffmpeg.av_interleaved_write_frame(outputFmt, encoderPkt);
                    if (ret < 0)
                    {
                        Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Error writing packet: {GetFFmpegError(ret)}");
                    }

                    ffmpeg.av_packet_unref(encoderPkt);
                }

                // Log packet writing progress occasionally
                if (recordingPts % 30 == 1 && packetsReceived > 0) // Every 30 frames
                {
                    Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Frame {recordingPts-1} wrote {packetsReceived} packets");
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"WriteFrameToRecording: Exception: {ex.Message}");
            }
        }

        private unsafe void FinalizeNativeRecording()
        {
            try
            {
                Tools.Logger.VideoLog.LogCall(this, "FinalizeNativeRecording: Starting finalization");
                
                if (outputFmt != null)
                {
                    // Write header if it hasn't been written yet (in case no frames were processed)
                    if (!headerWritten)
                    {
                        Tools.Logger.VideoLog.LogCall(this, "FinalizeNativeRecording: Writing header (no frames were processed)");
                        int headerResult = ffmpeg.avformat_write_header(outputFmt, null);
                        if (headerResult < 0)
                        {
                            Tools.Logger.VideoLog.LogCall(this, $"FinalizeNativeRecording: Failed to write header: {GetFFmpegError(headerResult)}");
                        }
                        else
                        {
                            headerWritten = true;
                        }
                    }

                    // Flush encoder
                    if (encoderCtx != null && encoderPkt != null)
                    {
                        Tools.Logger.VideoLog.LogCall(this, "FinalizeNativeRecording: Flushing encoder");
                        int sendResult = ffmpeg.avcodec_send_frame(encoderCtx, null); // Flush
                        if (sendResult < 0 && sendResult != ffmpeg.AVERROR_EOF)
                        {
                            Tools.Logger.VideoLog.LogCall(this, $"FinalizeNativeRecording: Error flushing encoder: {GetFFmpegError(sendResult)}");
                        }
                        
                        int ret;
                        int flushPackets = 0;
                        while ((ret = ffmpeg.avcodec_receive_packet(encoderCtx, encoderPkt)) >= 0)
                        {
                            if (videoStream != null)
                            {
                                ffmpeg.av_packet_rescale_ts(encoderPkt, encoderCtx->time_base, videoStream->time_base);
                                encoderPkt->stream_index = videoStream->index;
                                int writeResult = ffmpeg.av_interleaved_write_frame(outputFmt, encoderPkt);
                                if (writeResult < 0)
                                {
                                    Tools.Logger.VideoLog.LogCall(this, $"FinalizeNativeRecording: Error writing flush packet: {GetFFmpegError(writeResult)}");
                                }
                                else
                                {
                                    flushPackets++;
                                }
                            }
                            ffmpeg.av_packet_unref(encoderPkt);
                        }
                        Tools.Logger.VideoLog.LogCall(this, $"FinalizeNativeRecording: Flushed {flushPackets} packets");
                    }

                    // Write file trailer
                    if (headerWritten)
                    {
                        Tools.Logger.VideoLog.LogCall(this, "FinalizeNativeRecording: Writing trailer");
                        int trailerResult = ffmpeg.av_write_trailer(outputFmt);
                        if (trailerResult < 0)
                        {
                            Tools.Logger.VideoLog.LogCall(this, $"FinalizeNativeRecording: Error writing trailer: {GetFFmpegError(trailerResult)}");
                        }
                    }

                    // Close output file
                    if ((outputFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outputFmt->pb != null)
                    {
                        Tools.Logger.VideoLog.LogCall(this, "FinalizeNativeRecording: Closing output file");
                        ffmpeg.avio_closep(&outputFmt->pb);
                    }
                }

                // Generate .recordinfo.xml file
                GenerateRecordInfoFile(recordingFilename);

                Tools.Logger.VideoLog.LogCall(this, "Native recording finalized successfully");
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Exception finalizing native recording: {ex.Message}");
            }
            finally
            {
                CleanupNativeRecording();
            }
        }

        private unsafe void CleanupNativeRecording()
        {
            try
            {
                Tools.Logger.VideoLog.LogCall(this, "CleanupNativeRecording: Starting cleanup");
                
                if (encoderSws != null) 
                { 
                    ffmpeg.sws_freeContext(encoderSws); 
                    encoderSws = null; 
                    Tools.Logger.VideoLog.LogCall(this, "CleanupNativeRecording: SWS context freed");
                }
                
                if (encoderFrame != null) 
                { 
                    fixed (AVFrame** pEncoderFrame = &encoderFrame)
                    {
                        ffmpeg.av_frame_free(pEncoderFrame); 
                    }
                    encoderFrame = null; 
                    Tools.Logger.VideoLog.LogCall(this, "CleanupNativeRecording: Encoder frame freed");
                }
                
                if (encoderPkt != null) 
                { 
                    fixed (AVPacket** pEncoderPkt = &encoderPkt)
                    {
                        ffmpeg.av_packet_free(pEncoderPkt); 
                    }
                    encoderPkt = null; 
                    Tools.Logger.VideoLog.LogCall(this, "CleanupNativeRecording: Encoder packet freed");
                }
                
                if (encoderCtx != null) 
                { 
                    fixed (AVCodecContext** pEncoderCtx = &encoderCtx)
                    {
                        ffmpeg.avcodec_free_context(pEncoderCtx); 
                    }
                    encoderCtx = null; 
                    Tools.Logger.VideoLog.LogCall(this, "CleanupNativeRecording: Encoder context freed");
                }
                
                if (outputFmt != null)
                {
                    fixed (AVFormatContext** pOutputFmt = &outputFmt)
                    {
                        ffmpeg.avformat_free_context(*pOutputFmt);
                    }
                    outputFmt = null;
                    Tools.Logger.VideoLog.LogCall(this, "CleanupNativeRecording: Output format freed");
                }
                
                // Reset state variables
                headerWritten = false;
                encoderReady = false;
                videoStream = null;
                
                Tools.Logger.VideoLog.LogCall(this, "CleanupNativeRecording: Cleanup completed successfully");
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"CleanupNativeRecording: Exception during cleanup: {ex.Message}");
            }
        }

        private void GenerateRecordInfoFile(string videoFilePath)
        {
            try
            {
                // For now, just log that we would generate the file
                // The full implementation would depend on the application's recording info structure
                Tools.Logger.VideoLog.LogCall(this, $"Would generate .recordinfo.xml file for: {videoFilePath}");
                Tools.Logger.VideoLog.LogCall(this, $"Frame times count: {frameTimes.Count}");
                
                // TODO: Implement actual XML generation based on application requirements
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Failed to generate .recordinfo.xml file: {ex.Message}");
            }
        }

        public override void Dispose()
        {
            Tools.Logger.VideoLog.LogCall(this, "Disposing native AVFoundation frame source");
            
            if (Recording && nativeRecordingEnabled)
            {
                StopRecording();
            }
            
            Stop();
            CleanUp();
            
            base.Dispose();
        }

        public override void CleanUp()
        {
            run = false;
            readerThread = null;

            // Cleanup native recording
            CleanupNativeRecording();

            if (sws != null) { ffmpeg.sws_freeContext(sws); sws = null; }
            if (frame != null) { ffmpeg.av_frame_unref(frame); ffmpeg.av_free(frame); frame = null; }
            if (pkt != null) { ffmpeg.av_packet_unref(pkt); ffmpeg.av_free(pkt); pkt = null; }
            if (codecCtx != null) 
            { 
                var codecCtxPtr = codecCtx;
                ffmpeg.avcodec_free_context(&codecCtxPtr); 
                codecCtx = null; 
            }
            if (fmt != null)
            {
                var fmtPtr = fmt;
                ffmpeg.avformat_close_input(&fmtPtr);
                fmt = null;
            }
            if (rgbaHandle.IsAllocated) rgbaHandle.Free();

            base.CleanUp();
        }

        /// <summary>
        /// Try to access AVFoundation directly without relying on format registration
        /// Similar to how file playback works
        /// </summary>
        private bool TryDirectAVFoundationAccess()
        {
            try
            {
                Tools.Logger.VideoLog.LogCall(this, "Testing direct AVFoundation access...");
                
                // Allocate a format context like file playback does
                AVFormatContext* testFmt = ffmpeg.avformat_alloc_context();
                if (testFmt == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to allocate format context");
                    return false;
                }
                
                try
                {
                    // Try to open AVFoundation input directly, similar to file opening
                    // Use a simple device specification
                    string deviceUrl = "avfoundation:0"; // Camera index 0
                    
                    // Use null for input format - let FFmpeg auto-detect like file playback
                    // This bypasses the format registration requirement
                    var result = ffmpeg.avformat_open_input(&testFmt, deviceUrl, null, null);
                    
                    if (result >= 0)
                    {
                        Tools.Logger.VideoLog.LogCall(this, "✓ Direct AVFoundation open successful!");
                        
                        // Test if we can get stream info
                        if (ffmpeg.avformat_find_stream_info(testFmt, null) >= 0)
                        {
                            Tools.Logger.VideoLog.LogCall(this, "✓ Stream info detection successful!");
                            
                            // Check if we found video streams
                            bool hasVideo = false;
                            for (int i = 0; i < (int)testFmt->nb_streams; i++)
                            {
                                if (testFmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                                {
                                    hasVideo = true;
                                    Tools.Logger.VideoLog.LogCall(this, $"✓ Found video stream {i}");
                                    break;
                                }
                            }
                            
                            if (hasVideo)
                            {
                                Tools.Logger.VideoLog.LogCall(this, "✓ Direct AVFoundation access fully functional!");
                                return true;
                            }
                            else
                            {
                                Tools.Logger.VideoLog.LogCall(this, "No video streams found");
                                return false;
                            }
                        }
                        else
                        {
                            Tools.Logger.VideoLog.LogCall(this, "Stream info detection failed");
                            return false;
                        }
                    }
                    else
                    {
                        var error = GetFFmpegError(result);
                        Tools.Logger.VideoLog.LogCall(this, $"Direct AVFoundation open failed: {error}");
                        return false;
                    }
                }
                finally
                {
                    // Clean up test context
                    if (testFmt != null)
                    {
                        ffmpeg.avformat_close_input(&testFmt);
                    }
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Direct AVFoundation test exception: {ex.Message}");
                return false;
            }
        }

        private string GetFFmpegError(int error)
        {
            byte[] buffer = new byte[1024];
            unsafe
            {
                fixed (byte* ptr = buffer)
                {
                    ffmpeg.av_strerror(error, ptr, (ulong)buffer.Length);
                }
            }
            return System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        }
    }
}