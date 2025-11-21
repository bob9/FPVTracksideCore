using ImageServer;
using System;
using System.Collections.Generic;
using System.Linq;
using FFmpeg.AutoGen;
using Tools;

namespace FfmpegMediaPlatform
{
    /// <summary>
    /// macOS AVFoundation capture using native FFmpeg libraries (FFmpeg.AutoGen)
    /// Replaces process-based FfmpegAvFoundationFrameSource for better performance
    /// </summary>
    public class FfmpegLibAvFoundationFrameSource : FfmpegLibCaptureFrameSource
    {
        public FfmpegLibAvFoundationFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig)
            : base(ffmpegMediaFramework, videoConfig)
        {
        }

        public override IEnumerable<Mode> GetModes()
        {
            Tools.Logger.VideoLog.LogDebugCall(this, $"LibAVF GetModes() called - querying actual camera capabilities for '{VideoConfig.DeviceName}'");
            List<Mode> supportedModes = new List<Mode>();

            try
            {
                // Use invalid resolution to trigger ffmpeg to output supported modes
                string testArgs = $"-f avfoundation -framerate 30 -video_size 1234x5678 -i \"{VideoConfig.DeviceName}\"";
                Tools.Logger.VideoLog.LogDebugCall(this, $"LibAVF Querying supported modes with command: ffmpeg {testArgs}");

                var output = ffmpegMediaFramework.GetFfmpegText(testArgs, l =>
                    l.Contains("Supported modes:") ||
                    l.Contains("@[") ||
                    l.Contains("Selected video size") ||
                    l.Contains("Error opening"));

                bool foundSupportedModes = false;
                int index = 0;

                foreach (string line in output)
                {
                    Tools.Logger.VideoLog.LogDebugCall(this, $"LibAVF output: {line}");

                    if (line.Contains("Supported modes:"))
                    {
                        foundSupportedModes = true;
                        continue;
                    }

                    if (foundSupportedModes && line.Contains("@["))
                    {
                        // Parse lines like: "   640x480@[15.000000 30.000000]fps"
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)x(\d+)@\[([0-9.\s]+)\]fps");
                        if (match.Success)
                        {
                            int width = int.Parse(match.Groups[1].Value);
                            int height = int.Parse(match.Groups[2].Value);
                            string frameRatesStr = match.Groups[3].Value;

                            // Parse frame rates like "15.000000 30.000000"
                            var frameRates = frameRatesStr.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                .Select(fr => float.Parse(fr))
                                .ToList();

                            foreach (var frameRate in frameRates)
                            {
                                var mode = new Mode
                                {
                                    Width = width,
                                    Height = height,
                                    FrameRate = frameRate,
                                    FrameWork = FrameWork.FFmpeg,
                                    Index = index,
                                    Format = "uyvy422"
                                };
                                supportedModes.Add(mode);
                                Tools.Logger.VideoLog.LogDebugCall(this, $"LibAVF ✓ PARSED MODE: {width}x{height}@{frameRate}fps (Index {index})");
                                index++;
                            }
                        }
                    }
                }

                Tools.Logger.VideoLog.LogDebugCall(this, $"LibAVF Camera capability detection complete: {supportedModes.Count} supported modes found");

                if (supportedModes.Count == 0)
                {
                    Tools.Logger.VideoLog.LogDebugCall(this, "LibAVF WARNING: No supported modes detected for camera!");
                }
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException(this, ex);
            }

            return supportedModes;
        }

        protected override string GetInputFormatName()
        {
            return "avfoundation";
        }

        protected override unsafe void SetDeviceInputOptions(ref AVDictionary* options)
        {
            fixed (AVDictionary** pOptions = &options)
            {
                // Set pixel format
                ffmpeg.av_dict_set(pOptions, "pixel_format", "uyvy422", 0);

                // Set video size
                string videoSize = $"{VideoConfig.VideoMode.Width}x{VideoConfig.VideoMode.Height}";
                ffmpeg.av_dict_set(pOptions, "video_size", videoSize, 0);

                // Set frame rate - AVFoundation uses the camera's natural framerate by default
                // Only set if we have a specific requirement
                if (VideoConfig.VideoMode?.FrameRate > 0)
                {
                    string framerate = VideoConfig.VideoMode.FrameRate.ToString("F2");
                    ffmpeg.av_dict_set(pOptions, "framerate", framerate, 0);
                }

                Tools.Logger.VideoLog.LogDebugCall(this, $"LibAVF input options: pixel_format=uyvy422, video_size={videoSize}");
            }
        }

        protected override string GetDeviceIdentifier()
        {
            // For AVFoundation, use the device index or name
            // If ffmpegId is a number, use it directly, otherwise try to parse
            return VideoConfig.ffmpegId;
        }
    }
}
