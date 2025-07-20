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
    public class HttpStreamDecoderFrameSource : TextureFrameSource
    {
        private Process ffmpegProcess;
        private string streamUrl;
        private bool run;
        private Thread monitorThread;
        private bool connected;
        private int frameWidth;
        private int frameHeight;
        private float frameRate;
        private byte[] frameBuffer;
        private int frameBufferPosition;

        public override int FrameWidth => frameWidth;
        public override int FrameHeight => frameHeight;
        public override SurfaceFormat FrameFormat => SurfaceFormat.Color;

        public bool IsConnected => connected;

        public HttpStreamDecoderFrameSource(string httpStreamUrl, int width, int height, float fps)
            : base(new VideoConfig()) // Create a dummy VideoConfig
        {
            streamUrl = httpStreamUrl;
            frameWidth = width;
            frameHeight = height;
            frameRate = fps;
            
            // Use Color format (RGBA) which is universally supported
            SurfaceFormat = SurfaceFormat.Color;
            
            // Initialize frame buffer for YUV444p format (3 bytes per pixel)
            frameBuffer = new byte[width * height * 3]; // YUV444p format
            frameBufferPosition = 0;
            
            // Create RawTexture instances manually since XBuffer reflection doesn't work with the constructor
            var textures = new RawTexture[5];
            for (int i = 0; i < 5; i++)
            {
                textures[i] = new RawTexture(width, height);
            }
            rawTextures = new XBuffer<RawTexture>(textures);
        }

        public override bool Start()
        {
            if (run) return base.Start();

            run = true;
            connected = false;

            Logger.VideoLog.Log(this, $"Starting HTTP stream decoder for: {streamUrl}");

            // Start FFmpeg process to decode the HTTP stream
            StartFfmpegDecoder();

            // Start monitor thread
            monitorThread = new Thread(MonitorProcess);
            monitorThread.Name = "HttpStreamDecoderMonitor";
            monitorThread.Start();

            return base.Start();
        }

        public override bool Stop()
        {
            if (!run) return base.Stop();

            run = false;
            connected = false;

            Logger.VideoLog.Log(this, "Stopping HTTP stream decoder");

            // Stop FFmpeg process
            if (ffmpegProcess != null && !ffmpegProcess.HasExited)
            {
                try
                {
                    ffmpegProcess.StandardInput.WriteLine("q");
                    ffmpegProcess.WaitForExit(2000);
                }
                catch (Exception e)
                {
                    Logger.VideoLog.LogException(this, e);
                }

                if (!ffmpegProcess.HasExited)
                {
                    Logger.VideoLog.Log(this, "Force killing HTTP stream decoder");
                    try
                    {
                        ffmpegProcess.Kill();
                    }
                    catch (Exception e)
                    {
                        Logger.VideoLog.LogException(this, e);
                    }
                }
            }

            // Wait for monitor thread
            if (monitorThread != null)
            {
                monitorThread.Join(2000);
                monitorThread = null;
            }

            return base.Stop();
        }

        public override void Dispose()
        {
            Stop();
            base.Dispose();
        }

        private void StartFfmpegDecoder()
        {
            try
            {
                // Build FFmpeg command to decode the HTTP stream
                string ffmpegArgs = BuildFfmpegDecoderArgs();
                Logger.VideoLog.Log(this, $"FFmpeg decoder args: {ffmpegArgs}");

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = "ffmpeg";
                startInfo.Arguments = ffmpegArgs;
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.CreateNoWindow = true;

                ffmpegProcess = new Process();
                ffmpegProcess.StartInfo = startInfo;
                ffmpegProcess.EnableRaisingEvents = true;
                ffmpegProcess.Exited += OnProcessExited;

                ffmpegProcess.Start();
                
                // Start reading error output asynchronously
                ffmpegProcess.BeginErrorReadLine();
                ffmpegProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        Logger.VideoLog.LogCall(this, e.Data);
                    }
                };

                Logger.VideoLog.Log(this, $"HTTP stream decoder started for {streamUrl}");
                connected = true;
            }
            catch (Exception e)
            {
                Logger.VideoLog.LogException(this, e);
                connected = false;
            }
        }

        private string BuildFfmpegDecoderArgs()
        {
            // FFmpeg command to decode HTTP stream and output raw YUV444p frames
            // YUV444p is compatible with FFmpeg and we'll convert to RGBA32 efficiently
            return $"-i {streamUrl} " +
                   $"-c:v h264 " +
                   $"-f rawvideo -pix_fmt yuv444p " +
                   $"-s {frameWidth}x{frameHeight} " +
                   $"-";
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            if (run)
            {
                Logger.VideoLog.Log(this, "HTTP stream decoder process exited unexpectedly");
                connected = false;
                
                // Restart after a short delay
                Thread.Sleep(1000);
                if (run)
                {
                    Logger.VideoLog.Log(this, "Restarting HTTP stream decoder");
                    StartFfmpegDecoder();
                }
            }
        }

        private void MonitorProcess()
        {
            Logger.VideoLog.Log(this, "HTTP stream decoder monitor thread started");
            int frameCount = 0;
            
            while (run)
            {
                try
                {
                    if (ffmpegProcess != null && !ffmpegProcess.HasExited)
                    {
                        // Read raw video data from FFmpeg output
                        byte[] buffer = new byte[32768]; // Larger chunks for better performance
                        int bytesRead = 0;
                        
                        try
                        {
                            bytesRead = ffmpegProcess.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length);
                        }
                        catch (Exception readEx)
                        {
                            // Stream might be closed or not ready yet
                            Logger.VideoLog.Log(this, $"Read error: {readEx.Message}");
                            Thread.Sleep(100);
                            continue;
                        }
                            
                            if (bytesRead > 0)
                            {
                                // Copy data to frame buffer
                                int bytesToCopy = Math.Min(bytesRead, frameBuffer.Length - frameBufferPosition);
                                Array.Copy(buffer, 0, frameBuffer, frameBufferPosition, bytesToCopy);
                                frameBufferPosition += bytesToCopy;
                                
                                // Check if we have a complete frame
                                if (frameBufferPosition >= frameBuffer.Length)
                                {
                                    Logger.VideoLog.Log(this, $"HTTP stream decoder frame buffer complete: {frameBufferPosition}/{frameBuffer.Length} bytes");
                                    
                                    // Convert YUV444p to RGBA32 efficiently
                                    byte[] rgbaData = ConvertYuv444pToRgba32(frameBuffer, frameWidth, frameHeight);
                                    
                                    // Create texture from the converted RGBA data
                                    RawTexture rawTexture = new RawTexture(frameWidth, frameHeight);
                                    rawTexture.SetData(rgbaData, DateTime.Now.Ticks, frameCount);
                                    
                                    // Add to texture buffer
                                    rawTextures.WriteOne(rawTexture);
                                    
                                    // Trigger frame event
                                    long sampleTime = DateTime.Now.Ticks;
                                    Logger.VideoLog.Log(this, $"HTTP stream decoder triggering OnFrame event: sampleTime={sampleTime}, processNumber={frameCount}");
                                    OnFrame(sampleTime, frameCount);
                                    
                                    frameCount++;
                                    
                                    // Log every frame for debugging
                                    Logger.VideoLog.Log(this, $"HTTP stream decoder processed frame {frameCount} at {DateTime.Now:HH:mm:ss.fff}");
                                    
                                    if (frameCount % 30 == 0) // Log summary every 30 frames
                                    {
                                        Logger.VideoLog.Log(this, $"HTTP stream decoder processed {frameCount} frames total");
                                    }
                                    
                                    // Reset frame buffer for next frame
                                    frameBufferPosition = 0;
                                }
                            }
                    }
                    else if (ffmpegProcess != null && ffmpegProcess.HasExited)
                    {
                        Logger.VideoLog.Log(this, "HTTP stream decoder process has exited");
                        connected = false;
                        break;
                    }
                }
                catch (Exception e)
                {
                    Logger.VideoLog.LogException(this, e);
                    connected = false;
                }

                Thread.Sleep(16); // ~60fps polling rate for better responsiveness
            }
            
            Logger.VideoLog.Log(this, "HTTP stream decoder monitor thread stopped");
        }

        public override bool UpdateTexture(GraphicsDevice graphicsDevice, int drawFrameId, ref Texture2D texture)
        {
            // Add debugging for UI update frequency
            if (drawFrameId % 120 == 0) // Log every 120 UI updates for performance
            {
                Logger.VideoLog.Log(this, $"UI requesting texture update: frameId={drawFrameId}");
            }
            
            // Get the latest texture from the buffer
            RawTexture rawTexture;
            if (rawTextures.ReadOne(out rawTexture, drawFrameId))
            {
                // Create or get existing texture
                if (textures == null)
                {
                    textures = new Dictionary<GraphicsDevice, FrameTextureSample>();
                }

                FrameTextureSample frameTexture;
                if (!textures.TryGetValue(graphicsDevice, out frameTexture))
                {
                    frameTexture = new FrameTextureSample(graphicsDevice, frameWidth, frameHeight, SurfaceFormat);
                    textures.Add(graphicsDevice, frameTexture);
                    Logger.VideoLog.Log(this, $"Created new texture for graphics device: {frameWidth}x{frameHeight}");
                }

                // Update the texture with the raw data
                if (rawTexture.UpdateTexture(frameTexture))
                {
                    texture = frameTexture;
                    return true;
                }
                else
                {
                    Logger.VideoLog.Log(this, "Failed to update texture with raw data");
                }
            }
            else
            {
                // Log occasionally when no texture is available
                if (drawFrameId % 60 == 0)
                {
                    Logger.VideoLog.Log(this, $"No texture available for frame {drawFrameId}");
                }
            }
            return false;
        }

        public override IEnumerable<Mode> GetModes()
        {
            // Return a single mode with the current dimensions and frame rate
            return new List<Mode>
            {
                new Mode
                {
                    Width = frameWidth,
                    Height = frameHeight,
                    FrameRate = frameRate
                }
            };
        }

        private byte[] ConvertYuv444pToRgba32(byte[] yuvData, int width, int height)
        {
            // Efficient YUV444p to RGBA32 conversion
            byte[] rgbaData = new byte[width * height * 4];
            
            int ySize = width * height;
            int uSize = ySize;
            int vSize = ySize;
            
            int yIndex = 0;
            int uIndex = ySize;
            int vIndex = ySize + uSize;
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int rgbaIndex = (y * width + x) * 4;
                    
                    // Get Y, U, V values
                    int yVal = yuvData[yIndex++] & 0xFF;
                    int uVal = yuvData[uIndex++] & 0xFF;
                    int vVal = yuvData[vIndex++] & 0xFF;
                    
                    // Convert YUV to RGB using BT.601 standard
                    int c = yVal - 16;
                    int d = uVal - 128;
                    int e = vVal - 128;
                    
                    int r = (298 * c + 409 * e + 128) >> 8;
                    int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                    int b = (298 * c + 516 * d + 128) >> 8;
                    
                    // Clamp values
                    r = Math.Max(0, Math.Min(255, r));
                    g = Math.Max(0, Math.Min(255, g));
                    b = Math.Max(0, Math.Min(255, b));
                    
                    // Set RGBA values (A = 255 for full opacity)
                    rgbaData[rgbaIndex] = (byte)r;     // R
                    rgbaData[rgbaIndex + 1] = (byte)g; // G
                    rgbaData[rgbaIndex + 2] = (byte)b; // B
                    rgbaData[rgbaIndex + 3] = 255;     // A
                }
            }
            
            return rgbaData;
        }

    }
} 