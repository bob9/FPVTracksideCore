using Composition;
using Composition.Nodes;
using Microsoft.Xna.Framework;
using RaceLib;
using Tools;

namespace UI.Nodes
{
    // A pill saying whether the race is ready, live or complete (the modern header).
    public class RaceStatusNode : Node, IUpdateableNode
    {
        private RaceManager raceManager;
        private ColorNode background;
        private ColorNode dot;
        private TextNode text;
        private int state = -1;

        private static readonly Color ready = new Color(138, 153, 179);
        private static readonly Color complete = new Color(52, 211, 153);

        public RaceStatusNode(RaceManager raceManager)
        {
            this.raceManager = raceManager;

            background = new ColorNode(Color.Transparent);
            background.CornerRadius = 100;
            background.RingWidth = 1;
            AddChild(background);

            dot = new ColorNode(Color.White);
            dot.CornerRadius = 100;
            dot.RelativeBounds = new RectangleF(0.11f, 0.38f, 0.075f, 0.24f);
            dot.KeepAspectRatio = false;
            background.AddChild(dot);

            text = new TextNode("", Color.White);
            text.Style.Bold = true;
            text.Alignment = RectangleAlignment.CenterLeft;
            text.RelativeBounds = new RectangleF(0.24f, 0.27f, 0.66f, 0.46f);
            background.AddChild(text);
        }

        public void Update(GameTime gameTime)
        {
            int now = raceManager.RaceRunning ? 1 : raceManager.RaceFinished ? 2 : 0;
            if (now == state)
                return;
            state = now;

            Color c = now == 1 ? ShapeStyle.Danger : now == 2 ? complete : ready;
            dot.Color = c;
            text.Tint = now == 1 ? new Color(254, 205, 211) : now == 2 ? new Color(167, 243, 208) : ready;
            background.Color = new Color(c.R, c.G, c.B, (byte)(now == 0 ? 0 : 26));
            background.RingColor = new Color(c.R, c.G, c.B, (byte)90);
            text.Text = now == 1 ? Translator.Get("Label.Live", "LIVE") : now == 2 ? Translator.Get("Label.Complete", "COMPLETE") : Translator.Get("Label.Ready", "READY");
            RequestRedraw();
        }
    }
}
