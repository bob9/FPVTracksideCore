using Composition;
using Composition.Nodes;
using Microsoft.Xna.Framework;
using Tools;

namespace UI.Nodes
{
    // A small rounded label: "COMPLETE", "NEXT UP", a type badge.
    public class StatusPillNode : Node
    {
        private ColorNode background;
        private TextNode text;

        public StatusPillNode()
        {
            background = new ColorNode(Color.Transparent);
            background.CornerRadius = 6;
            AddChild(background);

            text = new TextNode("", Color.White);
            text.Style.Bold = true;
            text.RelativeBounds = new RectangleF(0.06f, 0.2f, 0.88f, 0.6f);
            background.AddChild(text);
        }

        public void Set(string label, Color fore, Color back)
        {
            text.Text = label;
            text.Tint = fore;
            background.Color = back;
            RequestRedraw();
        }
    }
}
