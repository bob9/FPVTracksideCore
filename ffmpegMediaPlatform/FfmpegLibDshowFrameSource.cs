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

        public FfmpegLibDshowFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig) : base(videoConfig)
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

            Tools.Logger.VideoLog.LogCall(this, $"Started native DirectShow RGBA recording to {filename}");
        }

        public void StopRecording()
        {
            if (!Recording)
            {
                Tools.Logger.VideoLog.LogCall(this, "Not currently recording");
                return;
            }

            Tools.Logger.VideoLog.LogCall(this, $"Stopping native DirectShow RGBA recording to {recordingFilename}");
            Recording = false;
            finalising = true;

            bool stopped = rgbaRecorderManager.StopRecording();
            if (!stopped)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Warning: RGBA recording may not have stopped cleanly");
            }

            finalising = false;
            Tools.Logger.VideoLog.LogCall(this, $"Stopped native DirectShow RGBA recording to {recordingFilename}");
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

        public override void Dispose()
        {
            Tools.Logger.VideoLog.LogCall(this, "Disposing native DirectShow frame source");
            
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