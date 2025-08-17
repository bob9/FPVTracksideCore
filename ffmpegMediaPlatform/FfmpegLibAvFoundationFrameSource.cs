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
        
        // RGBA recording using separate ffmpeg process
        private RgbaRecorderManager rgbaRecorderManager;
        
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
                if (rgbaRecorderManager != null && rgbaRecorderManager.FrameTimes.Length > 0)
                {
                    return rgbaRecorderManager.FrameTimes;
                }
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
            
            // Initialize RGBA recorder manager
            rgbaRecorderManager = new RgbaRecorderManager(ffmpegMediaFramework);
            manualRecording = false;
            finalising = false;
            recordingStartTime = DateTime.MinValue;
            frameCount = 0;

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
            // For native implementation, we need to probe AVFoundation devices
            // This mimics the functionality from FfmpegAvFoundationFrameSource
            Tools.Logger.VideoLog.LogCall(this, $"GetModes() called (NATIVE) - querying camera capabilities for '{VideoConfig.DeviceName}'");
            
            List<Mode> supportedModes = new List<Mode>();
            
            try
            {
                // Use the existing binary implementation to get modes for now
                // In the future, this could be replaced with native AVFoundation probing
                var tempSource = new FfmpegAvFoundationFrameSource(ffmpegMediaFramework, VideoConfig);
                var binaryModes = tempSource.GetModes();
                
                foreach (var mode in binaryModes)
                {
                    supportedModes.Add(new Mode
                    {
                        Width = mode.Width,
                        Height = mode.Height,
                        FrameRate = mode.FrameRate,
                        FrameWork = FrameWork.ffmpeg,
                        Index = mode.Index,
                        Format = mode.Format
                    });
                }
                
                Tools.Logger.VideoLog.LogCall(this, $"Camera capability detection complete (NATIVE): {supportedModes.Count} supported modes found");
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException(this, ex);
                // Return default modes if detection fails
                supportedModes.Add(new Mode { Width = 1920, Height = 1080, FrameRate = 30, FrameWork = FrameWork.ffmpeg, Format = "uyvy422" });
                supportedModes.Add(new Mode { Width = 1280, Height = 720, FrameRate = 60, FrameWork = FrameWork.ffmpeg, Format = "uyvy422" });
            }
            
            return supportedModes;
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
            
            // AVFoundation-specific options for better camera initialization
            ffmpeg.av_dict_set(&options, "probesize", "10000000", 0); // Increase probe size significantly
            ffmpeg.av_dict_set(&options, "analyzeduration", "5000000", 0); // 5 seconds to analyze stream
            
            // Real-time capture flags (but with better initialization)
            ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);
            ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);

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
                            // For camera initialization, frames may not be available immediately
                            retryCount++;
                            if (retryCount % 20 == 0) // Log every 20 attempts to reduce noise
                            {
                                Tools.Logger.VideoLog.LogCall(this, $"Camera warming up... (attempt {retryCount})");
                            }
                            
                            // Give up after 5 minutes (6000 attempts * 50ms = 5 minutes)
                            if (retryCount > 6000)
                            {
                                Tools.Logger.VideoLog.LogCall(this, "Camera failed to provide frames after 5 minutes - giving up");
                                Connected = false;
                                break;
                            }
                            
                            Thread.Sleep(50);
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
                    
                    // Log successful frame reception if we were retrying
                    if (retryCount > 0)
                    {
                        Tools.Logger.VideoLog.LogCall(this, $"✓ Camera frame received after {retryCount} retries!");
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
                        
                        // Handle recording
                        bool isRecording = Recording && rgbaRecorderManager.IsRecording;
                        if (isRecording)
                        {
                            // Queue frame for async recording
                            byte[] frameData = new byte[rgbaBuffer.Length];
                            Buffer.BlockCopy(rgbaBuffer, 0, frameData, 0, rgbaBuffer.Length);
                            // Queue the frame for recording (WriteFrame will be called by the recording worker)
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
            if (Recording)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Already recording to {recordingFilename}");
                return;
            }

            recordingFilename = filename;
            recordingStartTime = DateTime.MinValue;
            frameCount = 0;
            frameTimes.Clear();
            Recording = true;
            finalising = false;

            // Start RGBA recording with separate ffmpeg process
            float recordingFrameRate = (float)(frameRate > 0 ? frameRate : 30.0);
            
            bool started = rgbaRecorderManager.StartRecording(filename, FrameWidth, FrameHeight, recordingFrameRate, this);
            if (!started)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Failed to start RGBA recording to {filename}");
                Recording = false;
                return;
            }

            Tools.Logger.VideoLog.LogCall(this, $"Started native AVFoundation RGBA recording to {filename}");
        }

        public void StopRecording()
        {
            if (!Recording)
            {
                Tools.Logger.VideoLog.LogCall(this, "Not currently recording");
                return;
            }

            Tools.Logger.VideoLog.LogCall(this, $"Stopping native AVFoundation RGBA recording to {recordingFilename}");
            Recording = false;
            finalising = true;

            bool stopped = rgbaRecorderManager.StopRecording();
            if (!stopped)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Warning: RGBA recording may not have stopped cleanly");
            }

            finalising = false;
            Tools.Logger.VideoLog.LogCall(this, $"Stopped native AVFoundation RGBA recording to {recordingFilename}");
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

        public override void Dispose()
        {
            Tools.Logger.VideoLog.LogCall(this, "Disposing native AVFoundation frame source");
            
            // Stop and dispose RGBA recorder
            rgbaRecorderManager?.Dispose();
            
            Stop();
            CleanUp();
            
            base.Dispose();
        }

        public override void CleanUp()
        {
            run = false;
            readerThread = null;

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