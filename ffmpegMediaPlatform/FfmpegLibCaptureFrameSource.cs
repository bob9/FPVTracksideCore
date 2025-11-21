using ImageServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using Microsoft.Xna.Framework.Graphics;
using Tools;

namespace FfmpegMediaPlatform
{
    /// <summary>
    /// Base class for FFmpeg.AutoGen-based capture devices
    /// Replaces Process-based capture with native library calls for better performance and control
    /// </summary>
    public abstract class FfmpegLibCaptureFrameSource : TextureFrameSource, ICaptureFrameSource
    {
        protected int width;
        protected int height;
        protected FfmpegMediaFramework ffmpegMediaFramework;

        // FFmpeg native structures
        protected unsafe AVFormatContext* formatContext;
        protected unsafe AVCodecContext* codecContext;
        protected unsafe AVStream* videoStream;
        protected unsafe AVFrame* frame;
        protected unsafe AVFrame* rgbaFrame;
        protected unsafe AVPacket* packet;
        protected unsafe SwsContext* swsContext;

        // Threading
        protected byte[] buffer;
        protected Thread captureThread;
        protected bool run;
        protected bool inited;
        private volatile bool disposed = false;

        // PERFORMANCE: Frame queue for parallel processing
        private readonly System.Collections.Concurrent.ConcurrentQueue<(byte[] data, DateTime timestamp, long frameNumber)> frameProcessingQueue;
        private readonly System.Threading.SemaphoreSlim frameProcessingSemaphore;
        private Task frameProcessingTask;
        private readonly CancellationTokenSource frameProcessingCancellation;

        // Recording implementation
        protected string recordingFilename;
        private List<FrameTime> frameTimes;
        private bool recordNextFrameTime;
        private bool manualRecording;
        private bool finalising;
        private DateTime recordingStartTime;
        private long frameCount;

        // Frame timing tracking for camera loop
        private DateTime lastFrameTime = DateTime.MinValue;
        private float measuredFrameRate = 0f;
        private bool frameRateMeasured = false;

        // REAL-TIME: Smart frame dropping for immediate responsiveness
        private const int STARTUP_FRAMES_TO_DROP = 15;
        private const double MAX_DISPLAY_LATENCY_MS = 500;
        private readonly Queue<DateTime> displayFrameTimestamps = new Queue<DateTime>();
        private int framesDroppedForRealtime = 0;

        // Frame recording queue for async processing
        private readonly System.Collections.Concurrent.ConcurrentQueue<(byte[] frameData, DateTime captureTime, int frameNumber)> recordingQueue = new System.Collections.Concurrent.ConcurrentQueue<(byte[], DateTime, int)>();
        private readonly System.Threading.SemaphoreSlim recordingSemaphore = new System.Threading.SemaphoreSlim(0);
        private Task recordingWorkerTask;
        private CancellationTokenSource recordingCancellationSource;

        // RGBA recording using separate ffmpeg process
        protected LibavRecorderManager rgbaRecorderManager;

        public override int FrameWidth => width > 0 ? width : 640;
        public override int FrameHeight => height > 0 ? height : 480;
        public override SurfaceFormat FrameFormat => SurfaceFormat;

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
        public bool RecordNextFrameTime { set => recordNextFrameTime = value; }
        public bool ManualRecording { get => manualRecording; set => manualRecording = value; }
        public bool Finalising => finalising;

        static FfmpegLibCaptureFrameSource()
        {
            // Ensure FFmpeg native libraries are loaded
            FfmpegNativeLoader.EnsureRegistered();

            // Log available input formats for debugging
            LogAvailableInputFormats();
        }

        private static unsafe void LogAvailableInputFormats()
        {
            try
            {
                Tools.Logger.VideoLog.LogDebugCall("FfmpegLibCaptureFrameSource", "Checking available input formats...");

                // CRITICAL: Register all device inputs (avfoundation, dshow, etc.)
                // This must be called before av_find_input_format() will find device formats
                try
                {
                    FFmpeg.AutoGen.ffmpeg.avdevice_register_all();
                    Tools.Logger.VideoLog.LogDebugCall("FfmpegLibCaptureFrameSource", "avdevice_register_all() called successfully");
                }
                catch (Exception ex)
                {
                    Tools.Logger.VideoLog.LogException("FfmpegLibCaptureFrameSource", "Failed to register avdevice", ex);
                }

                // Check for specific device formats
                string[] deviceFormats = { "avfoundation", "dshow", "v4l2", "gdigrab" };
                foreach (var formatName in deviceFormats)
                {
                    var inputFormat = FFmpeg.AutoGen.ffmpeg.av_find_input_format(formatName);
                    Tools.Logger.VideoLog.LogDebugCall("FfmpegLibCaptureFrameSource",
                        $"Input format '{formatName}': {(inputFormat != null ? "AVAILABLE" : "NOT AVAILABLE")}");
                }

                // Check if avdevice is available
                try
                {
                    var version = FFmpeg.AutoGen.ffmpeg.avdevice_version();
                    Tools.Logger.VideoLog.LogDebugCall("FfmpegLibCaptureFrameSource", $"avdevice library version: {version} - AVAILABLE");
                }
                catch (Exception ex)
                {
                    Tools.Logger.VideoLog.LogException("FfmpegLibCaptureFrameSource", "avdevice library: NOT AVAILABLE", ex);
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException("FfmpegLibCaptureFrameSource", "Failed to check input formats", ex);
            }
        }

        public FfmpegLibCaptureFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig)
            : base(videoConfig)
        {
            this.ffmpegMediaFramework = ffmpegMediaFramework;

            // Initialize recording fields
            frameTimes = new List<FrameTime>();
            recordingFilename = null;
            recordNextFrameTime = false;

            // Initialize RGBA recorder manager
            rgbaRecorderManager = new LibavRecorderManager();
            manualRecording = false;
            finalising = false;
            recordingStartTime = DateTime.MinValue;
            frameCount = 0;

            // PERFORMANCE: Initialize frame processing queue
            frameProcessingQueue = new System.Collections.Concurrent.ConcurrentQueue<(byte[], DateTime, long)>();
            frameProcessingSemaphore = new System.Threading.SemaphoreSlim(0);
            frameProcessingCancellation = new CancellationTokenSource();

            // Set surface format - both platforms output RGBA
            SurfaceFormat = SurfaceFormat.Color;

            if (videoConfig.VideoMode == null || videoConfig.VideoMode.Index == -1)
            {
                videoConfig.VideoMode = ffmpegMediaFramework.DetectOptimalMode(GetModes());
            }

            // Calculate buffer size based on video mode
            if (videoConfig.VideoMode != null)
            {
                width = videoConfig.VideoMode.Width;
                height = videoConfig.VideoMode.Height;

                int bufferSize = width * height * 4; // RGBA = 4 bytes per pixel
                buffer = new byte[bufferSize];

                Tools.Logger.VideoLog.LogCall(this, $"LibCapture Initialized buffer: {width}x{height} RGBA = {bufferSize} bytes");
            }
            else
            {
                buffer = new byte[1280 * 720 * 4];
                Tools.Logger.VideoLog.LogDebugCall(this, $"LibCapture Using default buffer size: {buffer.Length} bytes");
            }

            if (width <= 0 || height <= 0)
            {
                width = 640;
                height = 480;
                Tools.Logger.VideoLog.LogCall(this, $"VideoConfig had invalid dimensions, using fallback 640x480");
            }

            buffer = new byte[width * height * 4];
            rawTextures = new XBuffer<RawTexture>(5, width, height);

            frameTimes = new List<FrameTime>();
            IsVisible = true;
            Direction = Directions.BottomUp;
        }

        /// <summary>
        /// Get device-specific input format name (e.g., "dshow", "avfoundation")
        /// </summary>
        protected abstract string GetInputFormatName();

        /// <summary>
        /// Get device-specific input options for av_dict_set
        /// </summary>
        protected abstract unsafe void SetDeviceInputOptions(ref AVDictionary* options);

        /// <summary>
        /// Get the device identifier for opening (e.g., "video=Device Name" for dshow, "0" for avfoundation)
        /// </summary>
        protected abstract string GetDeviceIdentifier();

        public override bool Start()
        {
            Tools.Logger.VideoLog.LogDebugCall(this, $"LibCapture Starting frame source for '{VideoConfig.DeviceName}' at {VideoConfig.VideoMode.Width}x{VideoConfig.VideoMode.Height}@{VideoConfig.VideoMode.FrameRate}fps");

            if (run)
            {
                Tools.Logger.VideoLog.LogDebugCall(this, "LibCapture Frame source already running, stopping first");
                Stop();
            }

            // Reset state for fresh start
            inited = false;
            width = VideoConfig.VideoMode?.Width ?? 640;
            height = VideoConfig.VideoMode?.Height ?? 480;
            buffer = new byte[width * height * 4];
            rawTextures = new XBuffer<RawTexture>(3, width, height);

            try
            {
                // Initialize FFmpeg capture device
                if (!InitializeCapture())
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to initialize FFmpeg capture");
                    return false;
                }

                run = true;
                Connected = true;

                // Start recording worker task for async frame processing
                StartRecordingWorkerTask();

                // Start capture thread with high priority for low latency
                captureThread = new Thread(CaptureLoop);
                captureThread.Name = "ffmpeglib - " + VideoConfig.DeviceName;
                captureThread.Priority = ThreadPriority.AboveNormal;
                captureThread.Start();

                // PERFORMANCE: Start parallel frame processing task
                frameProcessingTask = Task.Run(async () => await FrameProcessingLoop(frameProcessingCancellation.Token));

                Tools.Logger.VideoLog.LogCall(this, "LibCapture started successfully");
                return base.Start();
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException(this, ex);
                CleanupCapture();
                return false;
            }
        }

        /// <summary>
        /// Initialize FFmpeg capture device using native library
        /// </summary>
        private unsafe bool InitializeCapture()
        {
            try
            {
                // CRITICAL: Register all device inputs before trying to find them
                try
                {
                    ffmpeg.avdevice_register_all();
                    Tools.Logger.VideoLog.LogDebugCall(this, "avdevice_register_all() called in InitializeCapture");
                }
                catch (Exception ex)
                {
                    Tools.Logger.VideoLog.LogException(this, "Failed to call avdevice_register_all()", ex);
                }

                // Find input format
                string formatName = GetInputFormatName();
                AVInputFormat* inputFormat = ffmpeg.av_find_input_format(formatName);
                if (inputFormat == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Failed to find input format: {formatName} - FFmpeg libraries may not have device support compiled in. This will cause fallback to process-based capture.");
                    return false;
                }

                // Allocate format context
                formatContext = ffmpeg.avformat_alloc_context();
                if (formatContext == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to allocate format context");
                    return false;
                }

                // Set device-specific options
                AVDictionary* options = null;
                SetDeviceInputOptions(ref options);

                // Open input device
                string deviceId = GetDeviceIdentifier();
                AVFormatContext* ctx = formatContext;
                int ret = ffmpeg.avformat_open_input(&ctx, deviceId, inputFormat, &options);
                formatContext = ctx;

                if (ret < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Failed to open input device: {ret}");
                    return false;
                }

                // Find stream info
                ret = ffmpeg.avformat_find_stream_info(formatContext, null);
                if (ret < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Failed to find stream info: {ret}");
                    return false;
                }

                // Find video stream
                int videoStreamIndex = -1;
                for (int i = 0; i < formatContext->nb_streams; i++)
                {
                    if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        videoStreamIndex = i;
                        break;
                    }
                }

                if (videoStreamIndex == -1)
                {
                    Tools.Logger.VideoLog.LogCall(this, "No video stream found");
                    return false;
                }

                videoStream = formatContext->streams[videoStreamIndex];

                // Find decoder
                AVCodec* decoder = ffmpeg.avcodec_find_decoder(videoStream->codecpar->codec_id);
                if (decoder == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to find decoder");
                    return false;
                }

                // Allocate codec context
                codecContext = ffmpeg.avcodec_alloc_context3(decoder);
                if (codecContext == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to allocate codec context");
                    return false;
                }

                // Copy codec parameters
                ret = ffmpeg.avcodec_parameters_to_context(codecContext, videoStream->codecpar);
                if (ret < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Failed to copy codec parameters: {ret}");
                    return false;
                }

                // Open codec
                ret = ffmpeg.avcodec_open2(codecContext, decoder, null);
                if (ret < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Failed to open codec: {ret}");
                    return false;
                }

                // Update dimensions from actual stream
                width = codecContext->width;
                height = codecContext->height;
                buffer = new byte[width * height * 4];

                // Allocate frames
                frame = ffmpeg.av_frame_alloc();
                rgbaFrame = ffmpeg.av_frame_alloc();
                if (frame == null || rgbaFrame == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to allocate frames");
                    return false;
                }

                // Setup RGBA frame buffer
                rgbaFrame->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
                rgbaFrame->width = width;
                rgbaFrame->height = height;
                ret = ffmpeg.av_frame_get_buffer(rgbaFrame, 32);
                if (ret < 0)
                {
                    Tools.Logger.VideoLog.LogCall(this, $"Failed to allocate RGBA frame buffer: {ret}");
                    return false;
                }

                // Initialize SWS context for color conversion
                swsContext = ffmpeg.sws_getContext(
                    width, height, codecContext->pix_fmt,
                    width, height, AVPixelFormat.AV_PIX_FMT_RGBA,
                    2, null, null, null); // SWS_BILINEAR = 2

                if (swsContext == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to create SWS context");
                    return false;
                }

                // Allocate packet
                packet = ffmpeg.av_packet_alloc();
                if (packet == null)
                {
                    Tools.Logger.VideoLog.LogCall(this, "Failed to allocate packet");
                    return false;
                }

                rawTextures = new XBuffer<RawTexture>(3, width, height);
                inited = true;

                Tools.Logger.VideoLog.LogCall(this, $"LibCapture initialized: {width}x{height}, codec: {ffmpeg.avcodec_get_name(videoStream->codecpar->codec_id)}");
                return true;
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException(this, ex);
                return false;
            }
        }

        /// <summary>
        /// Main capture loop - reads frames from device using native FFmpeg
        /// </summary>
        private unsafe void CaptureLoop()
        {
            Tools.Logger.VideoLog.LogDebugCall(this, "LibCapture thread started");

            while (run && !disposed)
            {
                try
                {
                    if (!inited || formatContext == null)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    // Read packet from device
                    int ret = ffmpeg.av_read_frame(formatContext, packet);
                    if (ret < 0)
                    {
                        if (ret == ffmpeg.AVERROR_EOF)
                        {
                            Tools.Logger.VideoLog.LogDebugCall(this, "End of stream");
                            break;
                        }
                        // Ignore EAGAIN (-35) errors - common in native capture
                        if (ret != -35)
                        {
                            Tools.Logger.VideoLog.LogDebugCall(this, $"Error reading frame: {ret}");
                        }
                        continue;
                    }

                    // Only process video packets
                    if (packet->stream_index != videoStream->index)
                    {
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }

                    // Send packet to decoder
                    ret = ffmpeg.avcodec_send_packet(codecContext, packet);
                    if (ret < 0)
                    {
                        Tools.Logger.VideoLog.LogDebugCall(this, $"Error sending packet to decoder: {ret}");
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }

                    // Receive decoded frame
                    ret = ffmpeg.avcodec_receive_frame(codecContext, frame);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    {
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }
                    else if (ret < 0)
                    {
                        Tools.Logger.VideoLog.LogDebugCall(this, $"Error receiving frame from decoder: {ret}");
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }

                    // Convert to RGBA
                    ffmpeg.sws_scale(swsContext, frame->data, frame->linesize, 0, height,
                        rgbaFrame->data, rgbaFrame->linesize);

                    // Copy RGBA data to buffer
                    int stride = rgbaFrame->linesize[0];
                    byte* srcData = rgbaFrame->data[0];

                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(new IntPtr(srcData + y * stride), buffer, y * width * 4, width * 4);
                    }

                    // Process the captured frame
                    DateTime frameTimestamp = DateTime.UtcNow;
                    frameProcessingQueue.Enqueue((buffer, frameTimestamp, FrameProcessNumber + 1));
                    frameProcessingSemaphore.Release();

                    ProcessCameraFrame();

                    ffmpeg.av_packet_unref(packet);
                }
                catch (Exception ex)
                {
                    Tools.Logger.VideoLog.LogException(this, ex);
                    Thread.Sleep(100);
                }
            }

            Tools.Logger.VideoLog.LogDebugCall(this, "LibCapture thread finished");
        }

        /// <summary>
        /// PERFORMANCE: Parallel frame processing loop
        /// </summary>
        private async Task FrameProcessingLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && run)
                {
                    await frameProcessingSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                    if (frameProcessingQueue.TryDequeue(out var frameData))
                    {
                        await Task.Yield();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException(this, ex);
            }
        }

        /// <summary>
        /// Start recording worker task
        /// </summary>
        private void StartRecordingWorkerTask()
        {
            recordingCancellationSource = new CancellationTokenSource();
            recordingWorkerTask = Task.Run(async () =>
            {
                var cancellationToken = recordingCancellationSource.Token;

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await recordingSemaphore.WaitAsync(cancellationToken);

                        while (recordingQueue.TryDequeue(out var frameInfo))
                        {
                            try
                            {
                                rgbaRecorderManager?.WriteFrame(frameInfo.frameData, frameInfo.captureTime, frameInfo.frameNumber);
                            }
                            catch (Exception ex)
                            {
                                Tools.Logger.VideoLog.LogException(this, ex);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger.VideoLog.LogException(this, ex);
                    }
                }
            }, recordingCancellationSource.Token);
        }

        /// <summary>
        /// Process camera frame
        /// </summary>
        protected virtual void ProcessCameraFrame()
        {
            FrameProcessNumber++;

            bool isRecording = Recording && rgbaRecorderManager.IsRecording;
            bool shouldDropForDisplay = ShouldDropFrameForRealTime();

            if (isRecording)
            {
                QueueFrameForRecording();
            }

            if (!shouldDropForDisplay)
            {
                ProcessDisplayFrame();
            }
            else
            {
                UpdateFrameTimingStats();
            }
        }

        private bool ShouldDropFrameForRealTime()
        {
            if (FrameProcessNumber <= STARTUP_FRAMES_TO_DROP)
            {
                framesDroppedForRealtime++;
                return true;
            }

            DateTime now = DateTime.UtcNow;
            displayFrameTimestamps.Enqueue(now);

            while (displayFrameTimestamps.Count > 5)
                displayFrameTimestamps.Dequeue();

            if (displayFrameTimestamps.Count >= 5)
            {
                var oldestFrameTime = displayFrameTimestamps.Peek();
                var currentLatency = (now - oldestFrameTime).TotalMilliseconds;

                if (currentLatency > MAX_DISPLAY_LATENCY_MS)
                {
                    framesDroppedForRealtime++;
                    return true;
                }
            }

            return false;
        }

        private void UpdateFrameTimingStats()
        {
            DateTime currentFrameTime = DateTime.UtcNow;

            if (FrameProcessNumber % 60 == 0)
            {
                if (lastFrameTime != DateTime.MinValue)
                {
                    double actualInterval = (currentFrameTime - lastFrameTime).TotalMilliseconds / 60.0;
                    double actualFps = actualInterval > 0 ? 1000.0 / actualInterval : 0;
                    Tools.Logger.VideoLog.LogDebugCall(this, $"FRAME TIMING: Frame {FrameProcessNumber} - Actual: {actualFps:F2}fps");
                }
                lastFrameTime = currentFrameTime;
            }
        }

        private void ProcessDisplayFrame()
        {
            DateTime currentFrameTime = DateTime.UtcNow;

            if (FrameProcessNumber % 60 == 0)
            {
                double actualInterval = lastFrameTime != DateTime.MinValue ? (currentFrameTime - lastFrameTime).TotalMilliseconds / 60.0 : 0;
                double actualFps = actualInterval > 0 ? 1000.0 / actualInterval : 0;

                Tools.Logger.VideoLog.LogDebugCall(this, $"CAMERA TIMING: Frame {FrameProcessNumber} - Actual: {actualFps:F2}fps");

                if (FrameProcessNumber >= 180 && actualFps > 0)
                {
                    if (!frameRateMeasured)
                    {
                        measuredFrameRate = (float)actualFps;
                        frameRateMeasured = true;
                    }
                    else
                    {
                        float alpha = 0.1f;
                        measuredFrameRate = alpha * (float)actualFps + (1 - alpha) * measuredFrameRate;
                    }
                }

                lastFrameTime = currentFrameTime;
            }

            PrepareFrameForDisplay();
            NotifyReceivedFrame();
        }

        private void PrepareFrameForDisplay()
        {
            var currentRawTextures = rawTextures;
            if (currentRawTextures != null)
            {
                RawTexture rawFrame;
                if (currentRawTextures.GetWritable(out rawFrame))
                {
                    GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    try
                    {
                        IntPtr bufferPtr = handle.AddrOfPinnedObject();
                        rawFrame.SetData(bufferPtr, SampleTime, FrameProcessNumber);
                    }
                    finally
                    {
                        handle.Free();
                    }

                    currentRawTextures.WriteOne(rawFrame);
                }
            }
        }

        private void QueueFrameForRecording()
        {
            DateTime captureTime = UnifiedFrameTimingManager.GetHighPrecisionTimestamp();
            byte[] frameData = new byte[buffer.Length];
            Buffer.BlockCopy(buffer, 0, frameData, 0, buffer.Length);

            recordingQueue.Enqueue((frameData, captureTime, (int)FrameProcessNumber));
            recordingSemaphore.Release();
        }

        public void StartRecording(string filename)
        {
            filename += ".mp4";

            if (Recording)
            {
                Tools.Logger.VideoLog.LogDebugCall(this, $"Already recording to {recordingFilename}");
                return;
            }

            recordingFilename = filename;
            recordingStartTime = DateTime.MinValue;
            frameCount = 0;
            frameTimes.Clear();
            Recording = true;
            finalising = false;

            float recordingFrameRate = frameRateMeasured ? measuredFrameRate : (VideoConfig.VideoMode?.FrameRate ?? 30.0f);
            bool started = rgbaRecorderManager.StartRecording(filename, width, height, recordingFrameRate, this);

            if (!started)
            {
                Tools.Logger.VideoLog.LogCall(this, $"Failed to start recording to {filename}");
                Recording = false;
                return;
            }

            Tools.Logger.VideoLog.LogCall(this, $"Started recording to {filename}");
        }

        public void StopRecording()
        {
            if (!Recording)
            {
                Tools.Logger.VideoLog.LogCall(this, "Not currently recording");
                return;
            }

            Recording = false;
            finalising = true;

            bool stopped = rgbaRecorderManager.StopRecording();
            if (!stopped)
            {
                Tools.Logger.VideoLog.LogDebugCall(this, $"Warning: Recording may not have stopped cleanly");
            }

            finalising = false;
            Tools.Logger.VideoLog.LogCall(this, $"Stopped recording to {recordingFilename}");
        }

        protected override void ProcessImage()
        {
            if (recordNextFrameTime && !string.IsNullOrEmpty(VideoConfig.FilePath) && !VideoConfig.FilePath.Contains("hls"))
            {
                if (!Recording || !rgbaRecorderManager.IsRecording)
                {
                    if (frameTimes == null)
                    {
                        frameTimes = new List<FrameTime>();
                    }

                    DateTime frameTime = UnifiedFrameTimingManager.GetHighPrecisionTimestamp();

                    if (recordingStartTime == DateTime.MinValue)
                    {
                        recordingStartTime = frameTime;
                    }

                    var frameTimeEntry = UnifiedFrameTimingManager.CreateFrameTime(
                        (int)FrameProcessNumber, frameTime, recordingStartTime);
                    frameTimes.Add(frameTimeEntry);
                    frameCount++;
                }

                recordNextFrameTime = false;
            }
            else if (recordNextFrameTime)
            {
                recordNextFrameTime = false;
            }

            base.ProcessImage();
        }

        public override bool Stop()
        {
            Tools.Logger.VideoLog.LogDebugCall(this, $"LibCapture Stopping frame source for '{VideoConfig.DeviceName}'");
            run = false;

            // Wait for capture thread to finish
            if (captureThread != null && captureThread.IsAlive)
            {
                if (!captureThread.Join(5000))
                {
                    Tools.Logger.VideoLog.LogDebugCall(this, "LibCapture thread didn't finish in time");
                }
                captureThread = null;
            }

            return true;
        }

        private unsafe void CleanupCapture()
        {
            try
            {
                if (packet != null)
                {
                    fixed (AVPacket** pktPtr = &packet)
                    {
                        ffmpeg.av_packet_free(pktPtr);
                    }
                    packet = null;
                }

                if (frame != null)
                {
                    fixed (AVFrame** framePtr = &frame)
                    {
                        ffmpeg.av_frame_free(framePtr);
                    }
                    frame = null;
                }

                if (rgbaFrame != null)
                {
                    fixed (AVFrame** framePtr = &rgbaFrame)
                    {
                        ffmpeg.av_frame_free(framePtr);
                    }
                    rgbaFrame = null;
                }

                if (swsContext != null)
                {
                    ffmpeg.sws_freeContext(swsContext);
                    swsContext = null;
                }

                if (codecContext != null)
                {
                    fixed (AVCodecContext** ctxPtr = &codecContext)
                    {
                        ffmpeg.avcodec_free_context(ctxPtr);
                    }
                    codecContext = null;
                }

                if (formatContext != null)
                {
                    AVFormatContext* ctx = formatContext;
                    ffmpeg.avformat_close_input(&ctx);
                    formatContext = null;
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException(this, ex);
            }
        }

        public override void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            Tools.Logger.VideoLog.LogCall(this, $"LibCapture Disposing frame source for '{VideoConfig.DeviceName}'");

            // Stop recording worker task
            try
            {
                recordingCancellationSource?.Cancel();
                recordingWorkerTask?.Wait(TimeSpan.FromSeconds(5));
                recordingCancellationSource?.Dispose();
                recordingSemaphore?.Dispose();
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException(this, "Error stopping recording worker task", ex);
            }

            // Stop and dispose recorder
            rgbaRecorderManager?.Dispose();

            // Stop parallel processing
            frameProcessingCancellation?.Cancel();
            try
            {
                frameProcessingTask?.Wait(1000);
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException(this, $"Exception waiting for frame processing task", ex);
            }
            frameProcessingCancellation?.Dispose();
            frameProcessingSemaphore?.Dispose();

            Stop();
            CleanupCapture();

            base.Dispose();
        }
    }
}
