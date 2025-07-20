using ImageServer;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tools;

namespace FfmpegMediaPlatform
{
    public class HttpStreamFrameSource : FrameSource
    {
        private Process ffmpegProcess;
        private string streamUrl;
        private int streamPort;
        private static int nextPort = 8081;
        private bool run;
        private Thread monitorThread;
        private FfmpegMediaFramework ffmpegMediaFramework;
        private HttpStreamDecoderFrameSource decoderFrameSource;

        public string StreamUrl => streamUrl;
        public bool IsStreaming => ffmpegProcess != null && !ffmpegProcess.HasExited;

        public HttpStreamFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig)
            : base(videoConfig)
        {
            this.ffmpegMediaFramework = ffmpegMediaFramework;
            this.streamPort = Interlocked.Increment(ref nextPort);
            this.streamUrl = $"http://127.0.0.1:{streamPort}/feed";
        }

        public override bool Start()
        {
            if (run)
                return base.Start();

            Logger.VideoLog.Log(this, $"Starting HTTP stream for camera: {VideoConfig.DeviceName} on {streamUrl}");
            
            run = true;
            StartFfmpegStream();
            StartMonitorThread();
            
            // Create decoder frame source to decode the HTTP stream
            if (decoderFrameSource == null)
            {
                decoderFrameSource = new HttpStreamDecoderFrameSource(
                    streamUrl, 
                    VideoConfig.VideoMode.Width, 
                    VideoConfig.VideoMode.Height, 
                    VideoConfig.VideoMode.FrameRate);
                
                // Wire up the decoder's frame events to our frame events
                decoderFrameSource.OnFrameEvent += (sampleTime, processNumber) =>
                {
                    Logger.VideoLog.Log(this, $"HttpStreamFrameSource received frame event: sampleTime={sampleTime}, processNumber={processNumber}");
                    OnFrame(sampleTime, processNumber);
                };
            }
            
            // Start the decoder after a short delay to ensure the stream is ready
            Task.Delay(2000).ContinueWith(_ =>
            {
                if (run)
                {
                    decoderFrameSource.Start();
                }
            });
            
            return base.Start();
        }

        public override bool Stop()
        {
            run = false;
            
            // Stop the decoder frame source
            if (decoderFrameSource != null)
            {
                decoderFrameSource.Stop();
                decoderFrameSource.Dispose();
                decoderFrameSource = null;
            }
            
            if (monitorThread != null)
            {
                if (!monitorThread.Join(2000))
                {
                    Logger.VideoLog.Log(this, "Monitor thread did not exit gracefully");
                }
                monitorThread = null;
            }
            
            StopFfmpegStream();
            
            return base.Stop();
        }

        public override void Dispose()
        {
            Stop();
            base.Dispose();
        }

        private void StartFfmpegStream()
        {
            try
            {
                // Build FFmpeg command to create HTTP stream
                string ffmpegArgs = BuildFfmpegArgs();
                
                Logger.VideoLog.Log(this, $"FFmpeg HTTP stream args: {ffmpegArgs}");
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffmpegArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                ffmpegProcess = new Process();
                ffmpegProcess.StartInfo = startInfo;
                
                ffmpegProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        Logger.VideoLog.LogCall(this, e.Data);
                    }
                };

                ffmpegProcess.Start();
                ffmpegProcess.BeginErrorReadLine();
                
                Logger.VideoLog.Log(this, $"FFmpeg HTTP stream started for {VideoConfig.DeviceName} on {streamUrl}");
                Connected = true;
            }
            catch (Exception ex)
            {
                Logger.VideoLog.LogException(this, ex);
                Connected = false;
            }
        }

        private string BuildFfmpegArgs()
        {
            int width = VideoConfig.VideoMode.Width;
            int height = VideoConfig.VideoMode.Height;
            float framerate = VideoConfig.VideoMode.FrameRate;
            
            // Use hardware acceleration for lower resolutions, software for higher resolutions
            // VideoToolbox has limitations on resolution, so use software encoding for 720p+
            if (width <= 640 && height <= 480)
            {
                // Hardware acceleration for 720p and below
                return $"-f avfoundation -framerate {framerate} -video_size {width}x{height} -i \"{VideoConfig.ffmpegId}\" " +
                       $"-c:v h264_videotoolbox -profile:v baseline -level 3.0 " +
                       $"-b:v 4000k -maxrate 4000k -bufsize 8000k " +
                       $"-g {framerate} -keyint_min {framerate} " +
                       $"-preset ultrafast -tune zerolatency " +
                       $"-f mpegts -listen 1 {streamUrl}";
            }
            else
            {
                // Software encoding for 1080p and above
                // Use high422 profile to support UYVY422 camera format (4:2:2)
                return $"-f avfoundation -framerate {framerate} -video_size {width}x{height} -i \"{VideoConfig.ffmpegId}\" " +
                       $"-c:v libx264 -profile:v high422 -level 4.0 " +
                       $"-b:v 4000k -maxrate 4000k -bufsize 8000k " +
                       $"-g {framerate} -keyint_min {framerate} " +
                       $"-preset ultrafast -tune zerolatency " +
                       $"-f mpegts -listen 1 {streamUrl}";
            }
        }

        private void StopFfmpegStream()
        {
            if (ffmpegProcess != null)
            {
                try
                {
                    if (!ffmpegProcess.HasExited)
                    {
                        Logger.VideoLog.Log(this, "Stopping FFmpeg HTTP stream");
                        
                        // Try graceful shutdown first
                        try
                        {
                            ffmpegProcess.StandardInput.WriteLine("q");
                            ffmpegProcess.StandardInput.Flush();
                        }
                        catch
                        {
                            // Input stream might be closed
                        }
                        
                        if (!ffmpegProcess.WaitForExit(2000))
                        {
                            Logger.VideoLog.Log(this, "Force killing FFmpeg HTTP stream");
                            ffmpegProcess.Kill();
                            ffmpegProcess.WaitForExit(1000);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.VideoLog.LogException(this, ex);
                }
                finally
                {
                    ffmpegProcess?.Dispose();
                    ffmpegProcess = null;
                }
            }
            
            Connected = false;
        }

        private void StartMonitorThread()
        {
            monitorThread = new Thread(() =>
            {
                while (run)
                {
                    try
                    {
                        if (ffmpegProcess != null && ffmpegProcess.HasExited)
                        {
                            Logger.VideoLog.Log(this, "FFmpeg HTTP stream process exited unexpectedly");
                            Connected = false;
                            
                            if (run)
                            {
                                Logger.VideoLog.Log(this, "Restarting FFmpeg HTTP stream");
                                StartFfmpegStream();
                            }
                        }
                        
                        Thread.Sleep(1000);
                    }
                    catch (Exception ex)
                    {
                        Logger.VideoLog.LogException(this, ex);
                        Thread.Sleep(5000);
                    }
                }
            });
            
            monitorThread.Name = "HttpStreamMonitor";
            monitorThread.Start();
        }

        // These methods are required by FrameSource but not used for HTTP streaming
        public override bool UpdateTexture(GraphicsDevice graphicsDevice, int drawFrameId, ref Texture2D texture)
        {
            // Add debugging to see if UI is calling UpdateTexture
            if (drawFrameId % 30 == 0) // Log every 30 calls
            {
                Logger.VideoLog.Log(this, $"HttpStreamFrameSource UpdateTexture called: frameId={drawFrameId}, decoderFrameSource={decoderFrameSource != null}");
            }
            
            // Delegate to the decoder frame source
            if (decoderFrameSource != null)
            {
                bool result = decoderFrameSource.UpdateTexture(graphicsDevice, drawFrameId, ref texture);
                if (drawFrameId % 30 == 0) // Log every 30 calls
                {
                    Logger.VideoLog.Log(this, $"HttpStreamFrameSource UpdateTexture result: {result}, texture={texture != null}");
                }
                return result;
            }
            return false;
        }



        public override int FrameWidth => decoderFrameSource?.FrameWidth ?? VideoConfig.VideoMode.Width;
        public override int FrameHeight => decoderFrameSource?.FrameHeight ?? VideoConfig.VideoMode.Height;
        public override SurfaceFormat FrameFormat => decoderFrameSource?.FrameFormat ?? SurfaceFormat.Color;

        public override IEnumerable<Mode> GetModes()
        {
            // Use the same modes as the AVFoundation frame source
            return new FfmpegAvFoundationFrameSource(ffmpegMediaFramework, VideoConfig).GetModes();
        }
    }
} 