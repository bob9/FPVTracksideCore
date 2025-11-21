using ImageServer;
using System;
using System.Collections.Generic;
using System.Linq;
using FFmpeg.AutoGen;
using Tools;

namespace FfmpegMediaPlatform
{
    /// <summary>
    /// Windows DirectShow capture using native FFmpeg libraries (FFmpeg.AutoGen)
    /// Replaces process-based FfmpegDshowFrameSource for better performance
    /// </summary>
    public class FfmpegLibDshowFrameSource : FfmpegLibCaptureFrameSource
    {
        public FfmpegLibDshowFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig)
            : base(ffmpegMediaFramework, videoConfig)
        {
        }

        public override IEnumerable<Mode> GetModes()
        {
            Tools.Logger.VideoLog.LogDebugCall(this, $"LibDshow GetModes() called - querying actual camera capabilities for '{VideoConfig.DeviceName}'");
            List<Mode> supportedModes = new List<Mode>();

            try
            {
                // Use the existing ffmpeg binary method to query modes
                // The native library doesn't provide an easy way to enumerate modes
                string ffmpegListCommand = "-list_options true -f dshow -i video=\"" + VideoConfig.DeviceName + "\"";
                Tools.Logger.VideoLog.LogDebugCall(this, $"LibDshow COMMAND (list camera modes): ffmpeg {ffmpegListCommand}");

                IEnumerable<string> modes = ffmpegMediaFramework.GetFfmpegText(ffmpegListCommand, l => l.Contains("pixel_format") || l.Contains("vcodec="));

                int index = 0;
                var parsedModes = new List<(string format, int width, int height, float fps, int priority)>();

                foreach (string format in modes)
                {
                    Tools.Logger.VideoLog.LogDebugCall(this, $"LibDshow OUTPUT: {format}");

                    string videoFormat = ffmpegMediaFramework.GetValue(format, "vcodec");
                    int priority = 1;

                    if (string.IsNullOrEmpty(videoFormat))
                    {
                        videoFormat = ffmpegMediaFramework.GetValue(format, "pixel_format");
                        priority = 2;
                    }

                    if (videoFormat == "h264" || videoFormat == "mjpeg")
                    {
                        priority = 0;
                    }
                    else if (videoFormat == "uyvy422")
                    {
                        priority = 1;
                    }

                    string minSize = ffmpegMediaFramework.GetValue(format, "min s");
                    string maxSize = ffmpegMediaFramework.GetValue(format, "max s");

                    float minFps = 0, maxFps = 0;
                    var fpsMatches = System.Text.RegularExpressions.Regex.Matches(format, @"fps=([\d.]+)");

                    if (fpsMatches.Count >= 2)
                    {
                        float.TryParse(fpsMatches[0].Groups[1].Value, out minFps);
                        float.TryParse(fpsMatches[1].Groups[1].Value, out maxFps);
                    }
                    else if (fpsMatches.Count == 1)
                    {
                        if (float.TryParse(fpsMatches[0].Groups[1].Value, out minFps))
                        {
                            maxFps = minFps;
                        }
                    }

                    string[] minSizes = minSize.Split("x");
                    string[] maxSizes = maxSize.Split("x");

                    if (int.TryParse(minSizes[0], out int minX) && int.TryParse(minSizes[1], out int minY) &&
                        int.TryParse(maxSizes[0], out int maxX) && int.TryParse(maxSizes[1], out int maxY) &&
                        minFps > 0)
                    {
                        int width = minX;
                        int height = minY;

                        var supportedFrameRates = new List<float>();
                        supportedFrameRates.Add(minFps);

                        if (maxFps > minFps)
                        {
                            supportedFrameRates.Add(maxFps);
                        }

                        var commonRates = new float[] { 24, 25, 29.97f, 30, 50, 59.94f, 60 };
                        foreach (var rate in commonRates)
                        {
                            if (rate > minFps && rate < maxFps)
                            {
                                supportedFrameRates.Add(rate);
                            }
                        }

                        supportedFrameRates = supportedFrameRates.Distinct().OrderBy(f => f).ToList();

                        foreach (var fps in supportedFrameRates)
                        {
                            parsedModes.Add((videoFormat, width, height, fps, priority));
                        }
                    }
                }

                var sortedModes = parsedModes
                    .OrderBy(m => m.priority)
                    .ThenByDescending(m => m.width * m.height)
                    .ThenByDescending(m => m.fps)
                    .ToList();

                foreach (var mode in sortedModes)
                {
                    var videoMode = new Mode
                    {
                        Format = mode.format,
                        Width = mode.width,
                        Height = mode.height,
                        FrameRate = mode.fps,
                        FrameWork = FrameWork.FFmpeg,
                        Index = index
                    };
                    supportedModes.Add(videoMode);
                    Tools.Logger.VideoLog.LogDebugCall(this, $"LibDshow ✓ ADDED MODE: {mode.width}x{mode.height}@{mode.fps}fps ({mode.format}) (Index {index})");
                    index++;
                }

                Tools.Logger.VideoLog.LogDebugCall(this, $"LibDshow Camera capability detection complete: {supportedModes.Count} supported modes found");
            }
            catch (Exception ex)
            {
                Tools.Logger.VideoLog.LogException(this, ex);
            }

            return supportedModes;
        }

        protected override string GetInputFormatName()
        {
            return "dshow";
        }

        protected override unsafe void SetDeviceInputOptions(ref AVDictionary* options)
        {
            fixed (AVDictionary** pOptions = &options)
            {
                // Set video size
                string videoSize = $"{VideoConfig.VideoMode.Width}x{VideoConfig.VideoMode.Height}";
                ffmpeg.av_dict_set(pOptions, "video_size", videoSize, 0);

                // Set frame rate
                string framerate = VideoConfig.VideoMode.FrameRate.ToString("F2");
                ffmpeg.av_dict_set(pOptions, "framerate", framerate, 0);

                // Set pixel format or vcodec based on mode
                string format = VideoConfig.VideoMode.Format;
                if (!string.IsNullOrEmpty(format))
                {
                    if (format == "h264" || format == "mjpeg")
                    {
                        ffmpeg.av_dict_set(pOptions, "vcodec", format, 0);
                    }
                    else if (format != "uyvy422") // uyvy422 is default
                    {
                        ffmpeg.av_dict_set(pOptions, "pixel_format", format, 0);
                    }
                }

                // Set buffer size for better performance
                ffmpeg.av_dict_set(pOptions, "rtbufsize", "2048M", 0);

                Tools.Logger.VideoLog.LogDebugCall(this, $"LibDshow input options: video_size={videoSize}, framerate={framerate}, format={format}");
            }
        }

        protected override string GetDeviceIdentifier()
        {
            // For DirectShow, the format is "video=Device Name"
            return $"video={VideoConfig.ffmpegId}";
        }
    }
}
