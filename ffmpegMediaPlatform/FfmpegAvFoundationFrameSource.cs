using ImageServer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;

namespace FfmpegMediaPlatform
{
    public class FfmpegAvFoundationFrameSource : FfmpegFrameSource
    {
        public FfmpegAvFoundationFrameSource(FfmpegMediaFramework ffmpegMediaFramework, VideoConfig videoConfig)
            : base(ffmpegMediaFramework, videoConfig)
        {
        }

        public override IEnumerable<Mode> GetModes()
        {
            IEnumerable<string> modes = ffmpegMediaFramework.GetFfmpegText("-list_options true -f avfoundation -i video=\"" + VideoConfig.DeviceName + "\"", l => l.Contains("pixel_format"));

            int index = 0;
            //[dshow @ 000001ccc05aa180]   pixel_format=nv12  min s=1280x720 fps=30 max s=1280x720 fps=30
            foreach (string format in modes)
            {
                string pixelFormat = ffmpegMediaFramework.GetValue(format, "pixel_format");
                string size = ffmpegMediaFramework.GetValue(format, "min s");
                string fps = ffmpegMediaFramework.GetValue(format, "fps");

                string[] sizes = size.Split("x");
                if (int.TryParse(sizes[0], out int x) && int.TryParse(sizes[1], out int y) && float.TryParse(format, out float ffps))
                {
                    yield return new Mode { Format = pixelFormat, Width = x, Height = y, FrameRate = ffps, FrameWork = FrameWork.ffmpeg, Index = index };
                }
                index++;
            }
        }

        protected override ProcessStartInfo GetProcessStartInfo()
        {
            string name = VideoConfig.DeviceName;
            string index = VideoConfig.ffmpegId;
            
            // NATIVE macOS CAMERA PARAMETERS (using camera's actual native format):
            // -f avfoundation: Use native macOS camera framework
            // -framerate 30: Standard 30fps
            // -video_size 1280x720: Standard HD resolution
            // -i index: Camera device by index
            // -pix_fmt uyvy422: Camera's true native format (2 bytes per pixel) - no conversion, no override
            // -f rawvideo: Raw video output format
            // -: Output to stdout pipe
            
            string args = $"-f avfoundation -framerate 30 -video_size 1280x720 -i \"{index}\" -pix_fmt uyvy422 -f rawvideo -";
            
            Logger.VideoLog.Log(this, $"FFmpeg args: {args}");
            Console.WriteLine($"DEBUG: Starting FFmpeg with command: ffmpeg {args}");
            return ffmpegMediaFramework.GetProcessStartInfo(args);
        }
    }
}
