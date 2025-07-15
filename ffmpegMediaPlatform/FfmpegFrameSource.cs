using ImageServer;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Tools;

namespace FfmpegMediaPlatform
{

    public abstract class FfmpegFrameSource : TextureFrameSource
    {
        private int width;
        private int height;

        public override int FrameWidth
        {
            get
            {
                return width;
            }
        }

        public override int FrameHeight
        {
            get
            {
                return height;
            }
        }

        public override SurfaceFormat FrameFormat
        {
            get
            {
                return SurfaceFormat;
            }
        }

        protected FfmpegMediaFramework ffmpegMediaFramework;

        protected Process process;

        protected byte[] buffer;

        private Thread thread;
        private bool run;
        private bool inited;
        
        // Auto-recovery system for resilient operation
        private int processRestartCount = 0;
        private DateTime lastProcessStart = DateTime.MinValue;
        private readonly TimeSpan minProcessRunTime = TimeSpan.FromSeconds(5); // Minimum time before allowing restart
        private readonly int maxProcessRestarts = 10; // Maximum restarts before giving up

        public FfmpegFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig)
            : base(videoConfig)
        {
            this.ffmpegMediaFramework = ffmpegMediaFramework;
            // Use Color format (RGBA) which is universally supported across all platforms
            SurfaceFormat = SurfaceFormat.Color;
        }

        public override void Dispose()
        {
            run = false; // Stop the run loop first
            
            if (thread != null)
            {
                // Give the thread a reasonable time to exit gracefully
                if (!thread.Join(2000)) // 2 second timeout
                {
                    Logger.VideoLog.Log(this, "Frame reading thread did not exit gracefully, continuing with process cleanup");
                }
                thread = null;
            }
            
            CleanupProcess();
            base.Dispose();
        }

        private void CleanupProcess()
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.CancelErrorRead();
                        
                        // Try to terminate gracefully first
                        try
                        {
                            process.StandardInput.WriteLine("q"); // Send quit command to ffmpeg
                            process.StandardInput.Flush();
                        }
                        catch
                        {
                            // Input stream might already be closed
                        }
                        
                        // Wait a bit for graceful exit
                        if (!process.WaitForExit(1000)) // 1 second timeout
                        {
                            Logger.VideoLog.Log(this, "ffmpeg process did not exit gracefully, forcing termination");
                            process.Kill(); // Force kill if it doesn't exit gracefully
                            process.WaitForExit(1000); // Wait for kill to complete
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.VideoLog.LogException(this, ex);
                }
                finally
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors
                    }
                    process = null;
                }
            }
        }

        public override bool Start()
        {
            if (run)
                return base.Start();

            return StartProcess();
        }

        private bool StartProcess()
        {
            // Check if we should attempt restart based on failure history
            if (processRestartCount >= maxProcessRestarts)
            {
                Logger.VideoLog.Log(this, $"Maximum process restart limit ({maxProcessRestarts}) reached, giving up");
                Connected = false;
                return false;
            }

            // Prevent rapid restart cycles
            var timeSinceLastStart = DateTime.Now - lastProcessStart;
            if (timeSinceLastStart < minProcessRunTime && processRestartCount > 0)
            {
                Logger.VideoLog.Log(this, $"Process restarting too quickly (last start {timeSinceLastStart.TotalSeconds:F1}s ago), delaying restart");
                Task.Delay(3000).ContinueWith(_ => {
                    if (run) // Only restart if we're still supposed to be running
                    {
                        Logger.VideoLog.Log(this, "Attempting delayed process restart");
                        StartProcess();
                    }
                });
                return false;
            }

            CleanupProcess(); // Clean up any existing process
            inited = false;

            ProcessStartInfo processStartInfo = GetProcessStartInfo();

            process = new Process();
            process.StartInfo = processStartInfo;
            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    Logger.VideoLog.LogCall(this, e.Data);

                    //Stream #0:0: Video: rawvideo (YUY2 / 0x32595559), yuyv422(tv, bt470bg/bt709/unknown), 640x480, 60 fps, 60 tbr, 10000k tbn

                    if (!inited && e.Data.Contains("Stream"))
                    {
                        Regex reg = new Regex("([0-9]*)x([0-9]*), ([0-9]*) tbr");
                        Match m = reg.Match(e.Data);
                    if (m.Success) 
                    {
                        if (int.TryParse(m.Groups[1].Value, out int w) && int.TryParse(m.Groups[2].Value, out int h)) 
                        {
                            width = w;
                            height = h;
                        }

                        buffer = new byte[width * height * 2]; // UYVY422 = 2 bytes per pixel
                        rawTextures = new XBuffer<RawTexture>(5, width, height);

                        inited = true;
                        Logger.VideoLog.Log(this, $"Video stream initialized: {width}x{height} (UYVY422 input -> RGBA output)");
                        
                        // Reset restart counter on successful stream initialization (unconditional reset)
                        if (processRestartCount > 0)
                        {
                            Logger.VideoLog.Log(this, $"Video stream successfully initialized, resetting restart counter from {processRestartCount} to 0");
                        }
                        else
                        {
                            Logger.VideoLog.Log(this, $"Video stream successfully initialized, restart counter already at 0");
                        }
                        processRestartCount = 0; // Always reset on successful stream init
                    }
                }
                
                    // Check for device access errors and handle them gracefully
                    if (e.Data.Contains("Could not lock device for configuration") ||
                        e.Data.Contains("Input/output error") ||
                        e.Data.Contains("Error opening input"))
                    {
                        Logger.VideoLog.Log(this, "Camera device access error detected - another process may be using the camera");
                        // Don't set Connected = false here as that happens in the Run() method
                    }
                }
            };
            
            // Handle process exit with auto-recovery
            process.Exited += (s, e) =>
            {
                Logger.VideoLog.Log(this, $"ffmpeg process exited with code: {process.ExitCode}");
                
                // Don't immediately set Connected = false or run = false
                // Instead, attempt to restart the process if conditions are favorable
                if (run && processRestartCount < maxProcessRestarts)
                {
                    processRestartCount++;
                    Logger.VideoLog.Log(this, $"Attempting automatic process recovery (attempt {processRestartCount}/{maxProcessRestarts}) - exit code: {process.ExitCode}");
                    
                    // Small delay before restart to prevent rapid cycling
                    Task.Delay(1000).ContinueWith(_ => {
                        if (run) // Only restart if we're still supposed to be running
                        {
                            StartProcess();
                        }
                    });
                }
                else
                {
                    Logger.VideoLog.Log(this, $"Process restart conditions not met (run={run}, restartCount={processRestartCount}/{maxProcessRestarts}), marking as disconnected");
                    Connected = false;
                    run = false;
                }
            };
            process.EnableRaisingEvents = true;

            try
            {
                if (process.Start())
                {
                    lastProcessStart = DateTime.Now;
                    run = true;
                    Connected = true;

                    // Only create thread if it doesn't exist
                    if (thread == null)
                    {
                        thread = new Thread(Run);
                        thread.Name = "ffmpeg - " + VideoConfig.DeviceName;
                        thread.IsBackground = true; // Allow app to exit even if thread is running
                        thread.Start();
                    }

                    process.BeginErrorReadLine();
                    
                    // Give the process a moment to start and detect any immediate failures
                    Thread.Sleep(100);
                    
                    if (process.HasExited)
                    {
                        Logger.VideoLog.Log(this, "ffmpeg process failed to start or exited immediately");
                        Connected = false;
                        run = false;
                        return false;
                    }
                    
                    Logger.VideoLog.Log(this, $"FFmpeg process started successfully (PID: {process.Id})");
                    return base.Start();
                }
            }
            catch (Exception ex)
            {
                Logger.VideoLog.LogException(this, ex);
                Connected = false;
                run = false;
                return false;
            }

            return false;
        }

        public override bool Stop()
        {
            run = false;
            
            if (thread != null)
            {
                // Give the thread a reasonable time to exit gracefully
                if (!thread.Join(2000)) // 2 second timeout
                {
                    Logger.VideoLog.Log(this, "Frame reading thread did not exit gracefully, continuing with process cleanup");
                }
                thread = null;
            }
            
            CleanupProcess();
            return base.Stop();
        }

        protected abstract ProcessStartInfo GetProcessStartInfo();

        private void Run()
        {
            while(run)
            {
                if (!inited)
                {
                    Thread.Sleep(10); // Don't busy wait
                    continue;
                }
                
                // Check if process is still valid
                if (process == null || process.HasExited)
                {
                    Logger.VideoLog.Log(this, "Process is null or has exited, waiting for restart");
                    Thread.Sleep(1000); // Wait for potential restart
                    continue;
                }
                
                Stream reader = process.StandardOutput.BaseStream; // Use binary stream, not text StreamReader
                if (reader == null)
                {
                    Thread.Sleep(10);
                    continue;
                }

                try
                {
                    // Calculate frame size in bytes (UYVY422 = 2 bytes per pixel)
                    int frameSize = width * height * 2; // UYVY422 = 2 bytes per pixel
                    
                    if (buffer != null && buffer.Length >= frameSize)
                    {
                        // Use simple frame reading approach (based on working macOS example)
                        // Read the exact frame size in a straightforward loop
                        int totalRead = 0;
                        
                        try
                        {
                            while (totalRead < frameSize && run && !process.HasExited)
                            {
                                int bytesRead = reader.Read(buffer, totalRead, frameSize - totalRead);
                                if (bytesRead == 0)
                                {
                                    // No data available, process might be ending
                                    break;
                                }
                                totalRead += bytesRead;
                            }
                            
                            if (totalRead == frameSize)
                            {
                                ProcessImage();
                                
                                // Reset restart count on successful frame processing
                                if (processRestartCount > 0)
                                {
                                    var uptime = DateTime.Now - lastProcessStart;
                                    if (uptime > TimeSpan.FromSeconds(5)) // Reduced to 5 seconds for macOS 10-12s crash cycle compatibility
                                    {
                                        processRestartCount = 0; // Reset restart counter
                                        Logger.VideoLog.Log(this, $"Process stabilized for {uptime.TotalSeconds:F1}s, reset restart counter");
                                    }
                                }
                            }
                            else if (totalRead > 0)
                            {
                                Logger.VideoLog.Log(this, $"Incomplete frame read: {totalRead}/{frameSize} bytes - continuing");
                                // Just continue to next iteration
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.VideoLog.Log(this, $"Error reading frame: {ex.Message}");
                            continue; // Continue reading despite errors
                        }
                    }
                    else
                    {
                        Logger.VideoLog.Log(this, "Buffer not initialized properly");
                        Thread.Sleep(100);
                    }
                }
                catch (Exception ex)
                {
                    Logger.VideoLog.Log(this, $"Exception in frame reading: {ex.Message}, continuing...");
                    Thread.Sleep(100); // Delay on exception but don't restart
                    continue;
                }
            }
            
            Logger.VideoLog.Log(this, "Run loop ended");
        }

        private static int Clamp(int value)
        {
            return Math.Max(0, Math.Min(255, value));
        }

        protected override void ProcessImage()
        {
            var currentRawTextures = rawTextures;
            if (currentRawTextures != null)
            {
                RawTexture frame;
                if (currentRawTextures.GetWritable(out frame))
                {
                    // Convert UYVY422 to RGBA for MonoGame SurfaceFormat.Color compatibility
                    // UYVY422 = 2 bytes per pixel packed format (U Y0 V Y1 for 2 pixels) - true camera native
                    // RGBA = 4 bytes per pixel (R, G, B, A) - matching SurfaceFormat.Color
                    byte[] rgbaBuffer = new byte[width * height * 4];
                    
                    // Convert UYVY422 to RGBA (YUV to RGB conversion)
                    // UYVY422 packs 2 pixels into 4 bytes: [U Y0 V Y1]
                    // Process the entire frame sequentially
                    for (int pixelPair = 0; pixelPair < (width * height) / 2; pixelPair++)
                    {
                        // Calculate buffer indices
                        int uyvyIndex = pixelPair * 4; // 4 bytes per pixel pair in UYVY422
                        int rgba0Index = pixelPair * 8; // 8 bytes per pixel pair in RGBA (2 pixels * 4 bytes each)
                        int rgba1Index = rgba0Index + 4;
                        
                        // Extract UYVY values
                        int U = buffer[uyvyIndex] - 128;     // U (chrominance)
                        int Y0 = buffer[uyvyIndex + 1];      // Y0 (luminance pixel 0)
                        int V = buffer[uyvyIndex + 2] - 128; // V (chrominance)  
                        int Y1 = buffer[uyvyIndex + 3];      // Y1 (luminance pixel 1)
                        
                        // Convert YUV to RGB using ITU-R BT.601 conversion for pixel 0
                        int R0 = Clamp(Y0 + (int)(1.402 * V));
                        int G0 = Clamp(Y0 - (int)(0.344 * U) - (int)(0.714 * V));
                        int B0 = Clamp(Y0 + (int)(1.772 * U));
                        
                        // Convert YUV to RGB for pixel 1
                        int R1 = Clamp(Y1 + (int)(1.402 * V));
                        int G1 = Clamp(Y1 - (int)(0.344 * U) - (int)(0.714 * V));
                        int B1 = Clamp(Y1 + (int)(1.772 * U));
                        
                        // Store RGBA values for both pixels
                        rgbaBuffer[rgba0Index] = (byte)R0;
                        rgbaBuffer[rgba0Index + 1] = (byte)G0;
                        rgbaBuffer[rgba0Index + 2] = (byte)B0;
                        rgbaBuffer[rgba0Index + 3] = 255; // Alpha
                        
                        rgbaBuffer[rgba1Index] = (byte)R1;
                        rgbaBuffer[rgba1Index + 1] = (byte)G1;
                        rgbaBuffer[rgba1Index + 2] = (byte)B1;
                        rgbaBuffer[rgba1Index + 3] = 255; // Alpha
                    }
                    
                    FrameProcessNumber++;
                    frame.SetData(rgbaBuffer, SampleTime, FrameProcessNumber);
                    currentRawTextures.WriteOne(frame);
                }
            }

            base.ProcessImage();
        }
    }
}
