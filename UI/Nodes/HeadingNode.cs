using Composition.Nodes;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;
using UI;

namespace UI.Nodes
{
    public class HeadingNode : ColorNode
    {
        private TextNode title;

        public string Text { get { return title.Text; } }

        public HeadingNode(InfoPanelTheme infoPanelTheme, string text)
            :this(infoPanelTheme.Heading.XNA, infoPanelTheme.HeadingText.XNA, text) 
        {
        }

        public HeadingNode(Color background, Color textColor, string text)
            :base(background)
        {
            RelativeBounds = new Tools.RectangleF(0, 0.0f, 1, 0.05f);

            title = new TextNode(text, textColor);
            title.Alignment = RectangleAlignment.BottomLeft;
            title.RelativeBounds = new RectangleF(0.05f, 0.07f, 0.9f, 0.9f);
            AddChild(title);
        }

        // A card title: no band behind it, bold, a little smaller.
        public void StyleAsCardTitle()
        {
            Color = Color.Transparent;
            title.Style.Bold = true;
            title.Alignment = RectangleAlignment.CenterLeft;
            title.RelativeBounds = new RectangleF(0.02f, 0.2f, 0.9f, 0.6f);
        }
    }
}
