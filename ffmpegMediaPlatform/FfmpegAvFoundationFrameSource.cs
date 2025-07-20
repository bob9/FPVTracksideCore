using ImageServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tools;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FfmpegMediaPlatform
{
    public class FfmpegAvFoundationFrameSource : FfmpegFrameSource
    {
        public FfmpegAvFoundationFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig)
            : base(ffmpegMediaFramework, videoConfig)
        {
        }

        protected override ProcessStartInfo GetProcessStartInfo()
        {
            string ffmpegArgs;
            
            // For USB cameras, use a more reliable approach
            if (VideoConfig.DeviceName.Contains("USB Camera"))
            {
                // For USB cameras, specify uyvy422 format but let FFmpeg auto-select resolution
                // This works with most USB cameras and prevents "Configuration of video device failed" errors
                ffmpegArgs = $"-f avfoundation -framerate {VideoConfig.VideoMode.FrameRate} -i \"{VideoConfig.ffmpegId}\" -pix_fmt uyvy422 -f rawvideo -";
                Logger.VideoLog.Log(this, $"Using uyvy422 format with auto-resolution for USB camera: {VideoConfig.DeviceName}");
            }
            else if (VideoConfig.VideoMode.Width >= 1920 && VideoConfig.VideoMode.Height >= 1080)
            {
                // For 1080p, use yuv420p format which works better with MacBook Pro camera
                ffmpegArgs = $"-f avfoundation -framerate {VideoConfig.VideoMode.FrameRate} -video_size {VideoConfig.VideoMode.Width}x{VideoConfig.VideoMode.Height} -i \"{VideoConfig.ffmpegId}\" -pix_fmt yuv420p -f rawvideo -";
                Logger.VideoLog.Log(this, $"Using yuv420p format for 1080p compatibility");
            }
            else
            {
                // For lower resolutions, use uyvy422 for better performance
                ffmpegArgs = $"-f avfoundation -framerate {VideoConfig.VideoMode.FrameRate} -video_size {VideoConfig.VideoMode.Width}x{VideoConfig.VideoMode.Height} -i \"{VideoConfig.ffmpegId}\" -pix_fmt uyvy422 -f rawvideo -";
            }
            
            Logger.VideoLog.Log(this, $"FFmpeg args: {ffmpegArgs}");
            
            return new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = ffmpegArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        public override IEnumerable<Mode> GetModes()
        {
            Logger.VideoLog.Log(this, "GetModes() called - querying actual camera capabilities");
            Console.WriteLine("DEBUG: GetModes() called - querying actual camera capabilities");
            List<Mode> modes = new List<Mode>();
            
            try
            {
                // Query the actual camera capabilities using FFmpeg
                string deviceIndex = VideoConfig.ffmpegId;
                
                // First, try to get device information
                string deviceQueryCommand = $"-f avfoundation -list_devices true -i dummy";
                var deviceOutput = ffmpegMediaFramework.GetFfmpegText(deviceQueryCommand);
                
                Console.WriteLine($"DEBUG: Raw FFmpeg device list output: {string.Join(Environment.NewLine, deviceOutput)}");
                
                // Parse the device list output to extract actual supported modes
                var detectedModes = ParseCameraCapabilities(deviceOutput.ToList(), deviceIndex);
                if (detectedModes.Any())
                {
                    Logger.VideoLog.Log(this, $"Successfully detected {detectedModes.Count} modes for camera {VideoConfig.DeviceName}");
                    Console.WriteLine($"DEBUG: Successfully detected {detectedModes.Count} modes for camera {VideoConfig.DeviceName}");
                    modes.AddRange(detectedModes);
                }
                else
                {
                    Logger.VideoLog.Log(this, "No modes detected from device list, using fallback modes");
                    Console.WriteLine("DEBUG: No modes detected from device list, using fallback modes");
                    modes.AddRange(GetFallbackModes());
                }
                
                // Sort modes by resolution size and framerate
                var sortedModes = modes.OrderByDescending(m => m.Width * m.Height) // Sort by resolution size
                .ThenByDescending(m => m.FrameRate) // Then by framerate
                .ToList();
                
                // Update indices after sorting
                for (int i = 0; i < sortedModes.Count; i++)
                {
                    sortedModes[i].Index = i;
                }
                
                Console.WriteLine($"DEBUG: GetModes() returning {sortedModes.Count} modes");
                foreach (var mode in sortedModes)
                {
                    Console.WriteLine($"DEBUG: Mode {mode.Index}: {mode.Width}x{mode.Height}@{mode.FrameRate}fps");
                }
                
                return sortedModes;
            }
            catch (Exception ex)
            {
                Logger.VideoLog.Log(this, $"Error in GetModes(): {ex.Message}");
                Console.WriteLine($"DEBUG: Error in GetModes(): {ex.Message}");
                
                // Return fallback modes on error
                return GetFallbackModes();
            }
        }
        
        private List<Mode> ParseCameraCapabilities(List<string> output, string deviceIndex)
        {
            List<Mode> modes = new List<Mode>();
            
            try
            {
                Console.WriteLine($"DEBUG: ParseCameraCapabilities called for device index: {deviceIndex}");
                
                // The device list output from -list_devices doesn't contain capability info
                // We need to query the specific device for its capabilities  
                Console.WriteLine($"DEBUG: Querying device capabilities directly for device {deviceIndex}");
                
                // Query actual camera modes using a different approach
                var actualModes = QueryActualCameraModes(deviceIndex);
                if (actualModes.Count > 0)
                {
                    modes.AddRange(actualModes);
                    Console.WriteLine($"DEBUG: Found {actualModes.Count} actual camera modes for device {deviceIndex}");
                }
                else
                {
                    Console.WriteLine($"DEBUG: No actual modes detected for device {deviceIndex}, using smart fallback modes");
                    
                    // For USB cameras, provide realistic modes based on common USB camera capabilities
                    if (VideoConfig.DeviceName.Contains("USB Camera"))
                    {
                        // Common USB camera resolutions - prioritize what actually works
                        modes.Add(new Mode { Width = 640, Height = 480, FrameRate = 30, FrameWork = FrameWork.ffmpeg, Format = "uyvy422", Index = 0 });
                        modes.Add(new Mode { Width = 640, Height = 480, FrameRate = 15, FrameWork = FrameWork.ffmpeg, Format = "uyvy422", Index = 1 });
                        modes.Add(new Mode { Width = 320, Height = 240, FrameRate = 30, FrameWork = FrameWork.ffmpeg, Format = "uyvy422", Index = 2 });
                        modes.Add(new Mode { Width = 800, Height = 600, FrameRate = 15, FrameWork = FrameWork.ffmpeg, Format = "uyvy422", Index = 3 });
                        modes.Add(new Mode { Width = 1024, Height = 768, FrameRate = 10, FrameWork = FrameWork.ffmpeg, Format = "uyvy422", Index = 4 });
                        Console.WriteLine($"DEBUG: Added USB camera fallback modes for device {deviceIndex}");
                    }
                    else
                    {
                        // For built-in cameras, use the standard fallback modes
                        Console.WriteLine($"DEBUG: Using standard fallback modes for device {deviceIndex}");
                        return GetFallbackModes().ToList();
                    }
                }
                
                Logger.VideoLog.Log(this, $"Parsed {modes.Count} modes from camera capabilities");
                Console.WriteLine($"DEBUG: ParseCameraCapabilities returning {modes.Count} modes for device {deviceIndex}");
            }
            catch (Exception ex)
            {
                Logger.VideoLog.Log(this, $"Error parsing camera capabilities: {ex.Message}");
                Console.WriteLine($"DEBUG: Error parsing camera capabilities: {ex.Message}");
            }
            
            return modes;
        }
        
        private List<Mode> QueryActualCameraModes(string deviceIndex)
        {
            List<Mode> modes = new List<Mode>();
            
            try
            {
                Console.WriteLine($"DEBUG: QueryActualCameraModes for device index: {deviceIndex}");
                
                // For USB cameras, be more conservative with testing to avoid device conflicts
                if (VideoConfig.DeviceName.Contains("USB Camera"))
                {
                    Console.WriteLine($"DEBUG: USB camera detected, using conservative mode detection");
                    
                    // Test basic resolutions that most USB cameras support
                    var usbTestResolutions = new[]
                    {
                        new { Width = 640, Height = 480, FrameRate = 30 },
                        new { Width = 640, Height = 480, FrameRate = 15 },
                        new { Width = 320, Height = 240, FrameRate = 30 },
                    };
                    
                    foreach (var resolution in usbTestResolutions)
                    {
                        try
                        {
                            // Very brief test - just check if the device accepts the resolution
                            string testCommand = $"-f avfoundation -framerate {resolution.FrameRate} -video_size {resolution.Width}x{resolution.Height} -i {deviceIndex} -frames:v 1 -f null -";
                            
                            Console.WriteLine($"DEBUG: Testing USB camera mode: {resolution.Width}x{resolution.Height}@{resolution.FrameRate}fps");
                            var result = ffmpegMediaFramework.GetFfmpegText(testCommand);
                            
                            // Look for success indicators in the output
                            bool hasError = result.Any(line => 
                                line.Contains("Invalid") || 
                                line.Contains("not supported") ||
                                line.Contains("failed") ||
                                line.Contains("Cannot"));
                                
                            if (!hasError)
                            {
                                modes.Add(new Mode { 
                                    Width = resolution.Width, 
                                    Height = resolution.Height, 
                                    FrameRate = resolution.FrameRate, 
                                    FrameWork = FrameWork.ffmpeg, 
                                    Format = "uyvy422", 
                                    Index = modes.Count 
                                });
                                Console.WriteLine($"DEBUG: USB Camera supports {resolution.Width}x{resolution.Height}@{resolution.FrameRate}fps");
                            }
                            else
                            {
                                Console.WriteLine($"DEBUG: USB Camera does not support {resolution.Width}x{resolution.Height}@{resolution.FrameRate}fps");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"DEBUG: USB Camera test failed for {resolution.Width}x{resolution.Height}@{resolution.FrameRate}fps: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"DEBUG: Built-in camera detected, using standard mode detection");
                    
                    // For built-in cameras, test more resolutions
                    var standardTestResolutions = new[]
                    {
                        new { Width = 640, Height = 480, FrameRate = 30 },
                        new { Width = 1280, Height = 720, FrameRate = 30 },
                        new { Width = 1920, Height = 1080, FrameRate = 30 },
                    };
                    
                    foreach (var resolution in standardTestResolutions)
                    {
                        try
                        {
                            string testCommand = $"-f avfoundation -framerate {resolution.FrameRate} -video_size {resolution.Width}x{resolution.Height} -i {deviceIndex} -frames:v 1 -f null -";
                            
                            Console.WriteLine($"DEBUG: Testing camera mode: {resolution.Width}x{resolution.Height}@{resolution.FrameRate}fps");
                            var result = ffmpegMediaFramework.GetFfmpegText(testCommand);
                            
                            bool hasError = result.Any(line => 
                                line.Contains("Invalid") || 
                                line.Contains("not supported") ||
                                line.Contains("failed"));
                                
                            if (!hasError)
                            {
                                modes.Add(new Mode { 
                                    Width = resolution.Width, 
                                    Height = resolution.Height, 
                                    FrameRate = resolution.FrameRate, 
                                    FrameWork = FrameWork.ffmpeg, 
                                    Format = "uyvy422", 
                                    Index = modes.Count 
                                });
                                Console.WriteLine($"DEBUG: Camera supports {resolution.Width}x{resolution.Height}@{resolution.FrameRate}fps");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"DEBUG: Camera test failed for {resolution.Width}x{resolution.Height}@{resolution.FrameRate}fps: {ex.Message}");
                        }
                    }
                }
                
                Console.WriteLine($"DEBUG: QueryActualCameraModes found {modes.Count} working modes");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: Error in QueryActualCameraModes: {ex.Message}");
            }
            
            return modes;
        }
        
        private IEnumerable<Mode> GetFallbackModes()
        {
            List<Mode> modes = new List<Mode>();
            
            // Include all modes that the MacBook Pro camera supports, including 1080p
            // We'll work on making 1080p actually work instead of filtering it out
            
            var mode1 = new Mode();
            mode1.Width = 640;
            mode1.Height = 480;
            mode1.FrameRate = 15;
            mode1.FrameWork = FrameWork.ffmpeg;
            mode1.Index = 0;
            mode1.Format = "uyvy422";
            modes.Add(mode1);
            
            var mode2 = new Mode();
            mode2.Width = 640;
            mode2.Height = 480;
            mode2.FrameRate = 30;
            mode2.FrameWork = FrameWork.ffmpeg;
            mode2.Index = 1;
            mode2.Format = "uyvy422";
            modes.Add(mode2);
            
            var mode3 = new Mode();
            mode3.Width = 1280;
            mode3.Height = 720;
            mode3.FrameRate = 15;
            mode3.FrameWork = FrameWork.ffmpeg;
            mode3.Index = 2;
            mode3.Format = "uyvy422";
            modes.Add(mode3);
            
            var mode4 = new Mode();
            mode4.Width = 1280;
            mode4.Height = 720;
            mode4.FrameRate = 30;
            mode4.FrameWork = FrameWork.ffmpeg;
            mode4.Index = 3;
            mode4.Format = "uyvy422";
            modes.Add(mode4);
            
            // Include 1080p modes - we'll work on making them work
            var mode5 = new Mode();
            mode5.Width = 1920;
            mode5.Height = 1080;
            mode5.FrameRate = 15;
            mode5.FrameWork = FrameWork.ffmpeg;
            mode5.Index = 4;
            mode5.Format = "uyvy422";
            modes.Add(mode5);
            
            var mode6 = new Mode();
            mode6.Width = 1920;
            mode6.Height = 1080;
            mode6.FrameRate = 30;
            mode6.FrameWork = FrameWork.ffmpeg;
            mode6.Index = 5;
            mode6.Format = "uyvy422";
            modes.Add(mode6);
            
            return modes;
        }
    }
}
