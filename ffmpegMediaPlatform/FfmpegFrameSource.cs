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
        protected bool inited;
        protected byte[] buffer;
        private Thread thread;
        protected bool run;
        protected int processRestartCount = 0;
        protected DateTime streamStartTime;
        protected bool fallbackAttempted = false;
        
        // Auto-recovery system for resilient operation
        private DateTime lastProcessStart = DateTime.MinValue;
        private readonly TimeSpan minProcessRunTime = TimeSpan.FromSeconds(5); // Minimum time before allowing restart
        private readonly int maxProcessRestarts = 10; // Maximum restarts before giving up

        public FfmpegFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig)
            : base(videoConfig)
        {
            this.ffmpegMediaFramework = ffmpegMediaFramework;
            // Use Color format (RGBA) which is universally supported across all platforms
            SurfaceFormat = SurfaceFormat.Color;
            rawTextures = new XBuffer<RawTexture>(5, 640, 480); // Initialize with default size
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
            fallbackAttempted = false; // Reset fallback flag for new attempt
            streamStartTime = DateTime.Now; // Record start time for fallback logic

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
                        // Try to parse the stream information
                        // Handle both normal framerates and 1080p's "1000k tbr" case
                        Regex reg = new Regex("([0-9]*)x([0-9]*), ([0-9]*k?) tbr");
                        Match m = reg.Match(e.Data);
                        if (m.Success) 
                        {
                            if (int.TryParse(m.Groups[1].Value, out int w) && int.TryParse(m.Groups[2].Value, out int h)) 
                            {
                                width = w;
                                height = h;
                                Logger.VideoLog.Log(this, $"DEBUG: Setting dimensions from stream info: width={width}, height={height}");
                            }

                            // Calculate frame size for RGBA format (4 bytes per pixel)
                            int frameSize = width * height * 4;
                            
                            buffer = new byte[frameSize];
                            Logger.VideoLog.Log(this, $"DEBUG: Allocated buffer for {width}x{height} with 4 bytes per pixel (RGBA): {frameSize} bytes");
                            
                            // Update rawTextures with the correct dimensions
                            if (rawTextures != null)
                            {
                                rawTextures.Dispose();
                            }
                            rawTextures = new XBuffer<RawTexture>(5, width, height);
                            
                            inited = true;
                            Logger.VideoLog.Log(this, $"Video stream initialized: {width}x{height} (RGBA input -> RGBA output, no conversion needed)");
                            
                            // Check if this is 1080p with framerate detection issues
                            string framerateStr = m.Groups[3].Value;
                            if (framerateStr.Contains("k") && width == 1920 && height == 1080)
                            {
                                Logger.VideoLog.Log(this, $"1080p stream initialized with framerate detection issue ({framerateStr} tbr) - stream should work fine");
                                Console.WriteLine($"DEBUG: 1080p stream initialized with framerate detection issue ({framerateStr} tbr) - stream should work fine");
                            }
                            
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
                        else
                        {
                            // Check for framerate detection issues (common with 1080p)
                            if (e.Data.Contains("1000k tbr") || e.Data.Contains("not enough frames to estimate rate"))
                            {
                                Logger.VideoLog.Log(this, $"Framerate detection issue detected: {e.Data}");
                                Console.WriteLine($"DEBUG: Framerate detection issue detected: {e.Data}");
                                
                                // For 1080p, this is expected and the stream should still work
                                if (VideoConfig.VideoMode != null && 
                                    VideoConfig.VideoMode.Width == 1920 && 
                                    VideoConfig.VideoMode.Height == 1080)
                                {
                                    Logger.VideoLog.Log(this, "1080p framerate detection issue detected - this is expected, stream should work");
                                    Console.WriteLine("DEBUG: 1080p framerate detection issue detected - this is expected, stream should work");
                                    
                                    // Don't treat this as an error for 1080p - the stream should work fine
                                    // The framerate detection issue is cosmetic, not functional
                                }
                            }
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
            DateTime lastFrameTime = DateTime.Now;
            while(run)
            {
                if (!inited || width <= 0 || height <= 0)
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
                
                // Timeout logic
                if (DateTime.Now - lastFrameTime > TimeSpan.FromSeconds(10))
                {
                    Logger.VideoLog.Log(this, "No frame received in 10 seconds. Forcing process restart.");
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex) 
                    {
                        Logger.VideoLog.Log(this, "Error killing ffmpeg process on timeout");
                        Logger.VideoLog.LogException(this, ex);
                    }
                    // The process.Exited event will handle the restart
                    // We just need to wait here for the process to be restarted.
                    Thread.Sleep(1000);
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
                    // Calculate frame size in bytes based on actual buffer size and format
                    // The buffer was allocated based on the actual stream dimensions, not the reported ones
                    int actualFrameSize = buffer.Length;
                    
                    // RGBA format: 4 bytes per pixel
                    int expectedFrameSize = width * height * 4;
                    
                    // Add debugging for frame size issues
                    if (width == 1920 && height == 1080)
                    {
                        Logger.VideoLog.Log(this, $"DEBUG: Frame reading - width={width}, height={height}, actualFrameSize={actualFrameSize}, buffer.Length={buffer?.Length ?? 0}, format=rgba");
                    }
                    
                    if (buffer != null && buffer.Length >= actualFrameSize)
                    {
                        // Use simple frame reading approach (based on working macOS example)
                        // Read the exact frame size in a straightforward loop
                        int totalRead = 0;
                        
                        try
                        {
                            while (totalRead < actualFrameSize && run && !process.HasExited)
                            {
                                int bytesRead = reader.Read(buffer, totalRead, actualFrameSize - totalRead);
                                if (bytesRead == 0)
                                {
                                    // No data available, process might be ending
                                    break;
                                }
                                totalRead += bytesRead;
                            }
                            
                            if (totalRead == actualFrameSize)
                            {
                                lastFrameTime = DateTime.Now; // Update last frame time
                                ProcessImage();
                                
                                // Reset restart counter on successful frame processing
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
                                // Check if this is a resolution mismatch (common with 1080p cameras falling back to 720p)
                                // RGBA format: 4 bytes per pixel
                                int expectedFrameSizeForResolution = width * height * 4;
                                
                                // For RGBA: frameSize = width * height * 4, so width * height = totalRead / 4
                                int pixels = totalRead / 4;
                                
                                // Try to find reasonable width/height combinations
                                int actualWidth = 0, actualHeight = 0;
                                
                                // Common resolutions to check
                                int[] commonWidths = { 640, 1280, 1920, 2560, 3840 };
                                int[] commonHeights = { 480, 720, 1080, 1440, 2160 };
                                
                                foreach (int w in commonWidths)
                                {
                                    foreach (int h in commonHeights)
                                    {
                                        if (w * h == pixels)
                                        {
                                            actualWidth = w;
                                            actualHeight = h;
                                            break;
                                        }
                                    }
                                    if (actualWidth > 0) break;
                                }
                                
                                // If no common resolution found, try to estimate
                                if (actualWidth == 0)
                                {
                                    // Try to find a reasonable aspect ratio (16:9, 4:3, etc.)
                                    double aspectRatio = 16.0 / 9.0; // Assume 16:9
                                    actualHeight = (int)Math.Sqrt(pixels / aspectRatio);
                                    actualWidth = pixels / actualHeight;
                                    
                                    // Round to nearest multiple of 8 (common alignment requirement)
                                    actualWidth = (actualWidth / 8) * 8;
                                    actualHeight = (actualHeight / 8) * 8;
                                }
                                
                                if (actualWidth > 0 && actualHeight > 0 && (actualWidth != width || actualHeight != height))
                                {
                                    Logger.VideoLog.Log(this, $"Resolution mismatch detected: expected {width}x{height} ({expectedFrameSizeForResolution} bytes, RGBA), got {actualWidth}x{actualHeight} ({totalRead} bytes)");
                                    
                                    // Update dimensions to match actual output
                                    width = actualWidth;
                                    height = actualHeight;
                                    
                                    // Recalculate frame size with new dimensions (RGBA: 4 bytes per pixel)
                                    actualFrameSize = width * height * 4;
                                    
                                    // Reallocate buffer with correct size
                                    buffer = new byte[actualFrameSize];
                                    Logger.VideoLog.Log(this, $"Adjusted to actual resolution: {width}x{height} ({actualFrameSize} bytes)");
                                    
                                    // Update rawTextures with the correct dimensions
                                    if (rawTextures != null)
                                    {
                                        rawTextures.Dispose();
                                    }
                                    rawTextures = new XBuffer<RawTexture>(5, width, height);
                                    
                                    // Process this frame with the correct dimensions
                                    ProcessImage();
                                    lastFrameTime = DateTime.Now;
                                }
                                else
                                {
                                    Logger.VideoLog.Log(this, $"Incomplete frame read: {totalRead}/{expectedFrameSizeForResolution} bytes - continuing");
                                }
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
                        Logger.VideoLog.Log(this, $"Buffer not initialized properly - width={width}, height={height}, actualFrameSize={actualFrameSize}, buffer.Length={buffer?.Length ?? 0}");
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
                    // RGBA format: direct copy - no conversion needed
                    // Buffer already contains RGBA data (4 bytes per pixel)
                    FrameProcessNumber++;
                    frame.SetData(buffer, SampleTime, FrameProcessNumber);
                    currentRawTextures.WriteOne(frame);
                    NotifyReceivedFrame();
                }
            }

            base.ProcessImage();
        }
    }
}
