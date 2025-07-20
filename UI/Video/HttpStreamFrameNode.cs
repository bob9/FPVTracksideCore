using Composition;
using Composition.Nodes;
using ImageServer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;
using FfmpegMediaPlatform;

namespace UI.Video
{
    public class HttpStreamFrameNode : FrameNodeThumb
    {
        public HttpStreamFrameNode(HttpStreamFrameSource s)
            : base(s)
        {
            // Use the base class functionality - it will handle the video display
            // The HttpStreamFrameSource now provides actual video frames through the decoder
        }

        public override void Dispose()
        {
            // Base class handles the Source disposal
            base.Dispose();
        }

        public override void Draw(Drawer id, float parentAlpha)
        {
            // Use base class implementation
            base.Draw(id, parentAlpha);
        }

        public override void PreProcess(Drawer id)
        {
            // Call base class PreProcess
            base.PreProcess(id);
        }

        public new Rectangle Flip(Rectangle src)
        {
            // Use base class implementation
            return base.Flip(src);
        }



        public new void SaveImage(string filename)
        {
            // Not implemented for HTTP streams
        }
    }
} 