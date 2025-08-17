using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using ImageServer;
using Microsoft.Xna.Framework.Graphics;
using Tools;

namespace FfmpegMediaPlatform
{
    public unsafe class FfmpegLibDshowFrameSource : TextureFrameSource, ICaptureFrameSource
    {
        // Static constructor to ensure FFmpeg.AutoGen bindings are initialized
        static FfmpegLibDshowFrameSource()
        {
            try
            {
                if (!FfmpegGlobalInitializer.IsInitialized)
                {
                    FfmpegGlobalInitializer.Initialize();
                }
                // Skip av_log_set_level call due to compatibility issues
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FFmpeg.AutoGen initialization failed: {ex.Message}");
            }
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

        public FfmpegLibDshowFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig) : base(videoConfig)
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
                FfmpegNativeLoader.EnsureRegistered();
                
                if (ffmpeg.av_log_set_level == null)
                {
                    throw new NotSupportedException("FFmpeg native libraries not properly loaded");
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Native library initialization failed: {ex.Message}");
                throw new NotSupportedException($"FFmpeg native libraries not available: {ex.Message}", ex);
            }

            IsVisible = true;
        }

        public override IEnumerable<Mode> GetModes()
        {
            // For native implementation, we need to probe DirectShow devices
            // This mimics the functionality from FfmpegDshowFrameSource
            Tools.Logger.VideoLog.LogCall(this, $"GetModes() called (NATIVE) - querying camera capabilities for '{VideoConfig.DeviceName}'");
            
            List<Mode> supportedModes = new List<Mode>();
            
            try
            {
                // Use the existing binary implementation to get modes for now
                // In the future, this could be replaced with native DirectShow probing
                var tempSource = new FfmpegDshowFrameSource(ffmpegMediaFramework, VideoConfig);
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
            Tools.Logger.VideoLog.LogCall(this, "CAMERA ENGINE: ffmpeg LIB (in-process libav) - DirectShow");
            
            // Ensure native libraries are resolved
            FfmpegNativeLoader.EnsureRegistered();
            
            // Check if critical functions are available
            if (ffmpeg.avformat_open_input == null || ffmpeg.avformat_find_stream_info == null || 
                ffmpeg.avcodec_find_decoder == null || ffmpeg.avcodec_alloc_context3 == null)
            {
                throw new NotSupportedException("Critical FFmpeg functions not available - native libraries may be incompatible");
            }
            
            // Skip setting log level due to compatibility issues with FFmpeg.AutoGen
            Tools.Logger.VideoLog.LogCall(this, "Skipping av_log_set_level due to known compatibility issues");

            // Build DirectShow device URL
            string deviceName = VideoConfig.DeviceName;
            string deviceUrl = $"video={deviceName}";
            
            Tools.Logger.VideoLog.LogCall(this, $"Opening DirectShow device: {deviceUrl}");

            // Set up input format and options
            AVInputFormat* inputFormat = ffmpeg.av_find_input_format("dshow");
            if (inputFormat == null)
            {
                throw new Exception("DirectShow input format not found");
            }

            // Create format options dictionary
            AVDictionary* options = null;
            
            // Set video size
            string videoSize = $"{VideoConfig.VideoMode.Width}x{VideoConfig.VideoMode.Height}";
            ffmpeg.av_dict_set(&options, "video_size", videoSize, 0);
            
            // Set frame rate
            if (VideoConfig.VideoMode?.FrameRate > 0)
            {
                string frameRateStr = VideoConfig.VideoMode.FrameRate.ToString("F0");
                ffmpeg.av_dict_set(&options, "framerate", frameRateStr, 0);
            }
            
            // Set format-specific parameters (equivalent to binary implementation)
            string format = VideoConfig.VideoMode?.Format;
            if (!string.IsNullOrEmpty(format))
            {
                if (format == "h264" || format == "mjpeg")
                {
                    ffmpeg.av_dict_set(&options, "vcodec", format, 0);
                }
                else if (format != "uyvy422") // uyvy422 is default
                {
                    ffmpeg.av_dict_set(&options, "pixel_format", format, 0);
                }
            }
            
            // DirectShow-specific options for real-time capture
            ffmpeg.av_dict_set(&options, "rtbufsize", "2048M", 0);
            ffmpeg.av_dict_set(&options, "fflags", "nobuffer", 0);
            ffmpeg.av_dict_set(&options, "flags", "low_delay", 0);
            
            // Hardware decode acceleration if enabled
            if (VideoConfig.HardwareDecodeAcceleration && VideoConfig.IsCompressedVideoFormat)
            {
                ffmpeg.av_dict_set(&options, "hwaccel", "cuda", 0);
                Tools.Logger.VideoLog.LogCall(this, $"Hardware decode acceleration enabled for {format}");
            }

            Tools.Logger.VideoLog.LogCall(this, $"Opening input with options: video_size={videoSize}, format={format}");

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
            readerThread = new Thread(ReadLoop) { Name = "libav-dshow" };
            readerThread.Start();

            startTime = DateTime.UtcNow;
            Connected = true;
            
            Tools.Logger.VideoLog.LogCall(this, $"Native DirectShow camera started: {FrameWidth}x{FrameHeight}@{frameRate:F1}fps");
            
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
            Tools.Logger.VideoLog.LogCall(this, "Native DirectShow ReadLoop: Starting camera capture thread");
            
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
            
            Tools.Logger.VideoLog.LogCall(this, "Native DirectShow ReadLoop: Initialization complete, starting capture loop");
            
            while (run)
            {
                try
                {
                    int readResult = ffmpeg.av_read_frame(fmt, pkt);
                    if (readResult < 0)
                    {
                        // For live camera, EOF likely means device disconnected
                        Tools.Logger.VideoLog.LogCall(this, "Camera disconnected or read error");
                        Connected = false;
                        break;
                    }
                    
                    if (pkt->stream_index != videoStreamIndex)
                    {
                        ffmpeg.av_packet_unref(pkt);
                        continue;
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
            
            Tools.Logger.VideoLog.LogCall(this, "Native DirectShow ReadLoop: Camera capture thread finished");
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
                        
                        // Copy row by row into our pinned rgbaBuffer (flip vertically to fix upside-down issue)
                        int stride = codecCtx->width * 4;
                        byte* srcPtr = tmpData[0];
                        byte* dstPtr = (byte*)rgbaPtr.ToPointer();
                        for (int y = 0; y < codecCtx->height; y++)
                        {
                            // Flip the image by copying from bottom to top
                            int srcRow = codecCtx->height - 1 - y;
                            Buffer.MemoryCopy(srcPtr + srcRow * tmpLines[0], dstPtr + y * stride, stride, stride);
                        }
                        
                        // Handle native recording
                        if (Recording && nativeRecordingEnabled)
                        {
                            WriteFrameToRecording(dstPtr);
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
            recordingPts = 0;

            if (nativeRecordingEnabled)
            {
                // Use native hardware-accelerated recording
                if (InitializeNativeRecording(filename))
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Started native hardware-accelerated DirectShow recording to {filename}");
                    return;
                }
                else
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to initialize native recording, keeping recording disabled");
                    Recording = false;
                    return;
                }
            }
        }

        public void StopRecording()
        {
            if (!Recording)
            {
                Tools.Logger.VideoLog.LogCall(this, "Not currently recording");
                return;
            }

            Tools.Logger.VideoLog.LogCall(this, $"Stopping native hardware-accelerated DirectShow recording to {recordingFilename}");
            Recording = false;
            finalising = true;

            if (nativeRecordingEnabled)
            {
                FinalizeNativeRecording();
            }

            finalising = false;
            Tools.Logger.VideoLog.LogCall(this, $"Stopped native hardware-accelerated DirectShow recording to {recordingFilename}");
        }

        public override bool Stop()
        {
            Tools.Logger.VideoLog.LogCall(this, "Native DirectShow frame source stopping");
            
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

        private unsafe bool InitializeNativeRecording(string filename)
        {
            try
            {
                // Ensure output directory exists
                string outputDir = Path.GetDirectoryName(filename);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
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

                // Find H.264 encoder (try hardware first, fallback to software)
                AVCodec* codec = null;
                string codecName = "h264_nvenc"; // NVIDIA hardware encoder
                
                codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
                if (codec == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "NVENC not available, trying h264_amf (AMD)");
                    codecName = "h264_amf";
                    codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
                }
                
                if (codec == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Hardware encoders not available, using software h264");
                    codecName = "libx264";
                    codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
                }
                
                if (codec == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "No H.264 encoder found");
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

                // Configure encoder
                encoderCtx->width = FrameWidth;
                encoderCtx->height = FrameHeight;
                encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                // Configure for variable frame rate (VFR) to support timestamp-based recording
                // Use high-resolution time base for precise PTS timing
                encoderCtx->time_base = new AVRational { num = 1, den = 90000 }; // 90kHz time base (standard for H.264)
                encoderCtx->framerate = new AVRational { num = 0, den = 1 }; // 0 indicates variable frame rate
                
                // Hardware encoder specific settings
                if (codecName.Contains("nvenc"))
                {
                    // NVENC settings for low latency
                    ffmpeg.av_opt_set(encoderCtx->priv_data, "preset", "llhp", 0); // Low latency, high performance
                    ffmpeg.av_opt_set(encoderCtx->priv_data, "tune", "zerolatency", 0);
                    ffmpeg.av_opt_set(encoderCtx->priv_data, "rc", "cbr", 0); // Constant bitrate
                    encoderCtx->bit_rate = 5000000; // 5 Mbps
                }
                else if (codecName.Contains("amf"))
                {
                    // AMD AMF settings
                    ffmpeg.av_opt_set(encoderCtx->priv_data, "usage", "lowlatency", 0);
                    ffmpeg.av_opt_set(encoderCtx->priv_data, "profile", "high", 0);
                    encoderCtx->bit_rate = 5000000;
                }
                else
                {
                    // Software encoder settings
                    ffmpeg.av_opt_set(encoderCtx->priv_data, "preset", "medium", 0);
                    ffmpeg.av_opt_set(encoderCtx->priv_data, "tune", "zerolatency", 0);
                    encoderCtx->bit_rate = 5000000;
                }

                // Set GOP size for keyframes
                encoderCtx->gop_size = Math.Max(1, (int)(frameRate * 0.1f)); // Keyframe every 0.1s

                // Copy parameters to stream
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

                // Open encoder
                if (ffmpeg.avcodec_open2(encoderCtx, codec, null) < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to open encoder");
                    return false;
                }

                // Allocate frame and packet
                encoderFrame = ffmpeg.av_frame_alloc();
                encoderPkt = ffmpeg.av_packet_alloc();
                
                if (encoderFrame == null || encoderPkt == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to allocate encoder frame or packet");
                    return false;
                }

                // Configure frame
                encoderFrame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                encoderFrame->width = FrameWidth;
                encoderFrame->height = FrameHeight;
                
                if (ffmpeg.av_frame_get_buffer(encoderFrame, 32) < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to allocate encoder frame buffer");
                    return false;
                }

                // Initialize SWS context for RGBA to YUV420P conversion
                encoderSws = ffmpeg.sws_getContext(
                    FrameWidth, FrameHeight, AVPixelFormat.AV_PIX_FMT_RGBA,
                    FrameWidth, FrameHeight, AVPixelFormat.AV_PIX_FMT_YUV420P,
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

                // Write file header
                if (ffmpeg.avformat_write_header(outputFmt, null) < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to write file header");
                    return false;
                }

                Tools.Logger.VideoLog.LogCall(this, $"Native recording initialized successfully using {codecName}");
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
            if (!Recording || encoderCtx == null || encoderFrame == null) return;

            try
            {
                // Convert RGBA to YUV420P
                byte_ptrArray4 srcData = new byte_ptrArray4();
                int_array4 srcLinesize = new int_array4();
                srcData[0] = rgbaData;
                srcLinesize[0] = FrameWidth * 4; // RGBA stride

                ffmpeg.sws_scale(encoderSws, srcData, srcLinesize, 0, FrameHeight,
                    encoderFrame->data, encoderFrame->linesize);

                // Set frame timing using actual timestamps instead of frame counter
                DateTime currentFrameTime = DateTime.Now;
                TimeSpan timeSinceRecordingStart = currentFrameTime - recordingStartTime;
                
                // Convert timestamp to encoder time_base units for proper PTS
                double timebaseSeconds = (double)encoderCtx->time_base.num / encoderCtx->time_base.den;
                long calculatedPts = (long)(timeSinceRecordingStart.TotalSeconds / timebaseSeconds);
                
                encoderFrame->pts = calculatedPts;

                // Encode frame
                int ret = ffmpeg.avcodec_send_frame(encoderCtx, encoderFrame);
                if (ret < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Error sending frame to encoder: {GetFFmpegError(ret)}");
                    return;
                }

                // Retrieve encoded packets
                while (ret >= 0)
                {
                    ret = ffmpeg.avcodec_receive_packet(encoderCtx, encoderPkt);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        break;
                    
                    if (ret < 0)
                    {
                        Tools.Logger.VideoLog.LogCall(this, $"Error receiving packet from encoder: {GetFFmpegError(ret)}");
                        break;
                    }

                    // Rescale packet timing
                    ffmpeg.av_packet_rescale_ts(encoderPkt, encoderCtx->time_base, videoStream->time_base);
                    encoderPkt->stream_index = videoStream->index;

                    // Write packet to file
                    ret = ffmpeg.av_interleaved_write_frame(outputFmt, encoderPkt);
                    if (ret < 0)
                    {
                        Tools.Logger.VideoLog.LogCall(this, $"Error writing packet: {GetFFmpegError(ret)}");
                    }

                    ffmpeg.av_packet_unref(encoderPkt);
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Exception writing frame to recording: {ex.Message}");
            }
        }

        private unsafe void FinalizeNativeRecording()
        {
            try
            {
                if (outputFmt != null)
                {
                    // Flush encoder
                    if (encoderCtx != null)
                    {
                        ffmpeg.avcodec_send_frame(encoderCtx, null); // Flush
                        
                        int ret;
                        while ((ret = ffmpeg.avcodec_receive_packet(encoderCtx, encoderPkt)) >= 0)
                        {
                            ffmpeg.av_packet_rescale_ts(encoderPkt, encoderCtx->time_base, videoStream->time_base);
                            encoderPkt->stream_index = videoStream->index;
                            ffmpeg.av_interleaved_write_frame(outputFmt, encoderPkt);
                            ffmpeg.av_packet_unref(encoderPkt);
                        }
                    }

                    // Write file trailer
                    ffmpeg.av_write_trailer(outputFmt);

                    // Close output file
                    if ((outputFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    {
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
            if (encoderSws != null) { ffmpeg.sws_freeContext(encoderSws); encoderSws = null; }
            if (encoderFrame != null) 
            { 
                fixed (AVFrame** pEncoderFrame = &encoderFrame)
                {
                    ffmpeg.av_frame_free(pEncoderFrame); 
                }
                encoderFrame = null; 
            }
            if (encoderPkt != null) 
            { 
                fixed (AVPacket** pEncoderPkt = &encoderPkt)
                {
                    ffmpeg.av_packet_free(pEncoderPkt); 
                }
                encoderPkt = null; 
            }
            if (encoderCtx != null) 
            { 
                fixed (AVCodecContext** pEncoderCtx = &encoderCtx)
                {
                    ffmpeg.avcodec_free_context(pEncoderCtx); 
                }
                encoderCtx = null; 
            }
            if (outputFmt != null)
            {
                fixed (AVFormatContext** pOutputFmt = &outputFmt)
                {
                    ffmpeg.avformat_free_context(*pOutputFmt);
                }
                outputFmt = null;
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
            Tools.Logger.VideoLog.LogCall(this, "Disposing native DirectShow frame source");
            
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