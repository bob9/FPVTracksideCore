using Composition;
using Composition.Nodes;
using Microsoft.Xna.Framework;
using RaceLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;

namespace UI.Nodes
{
    public class PilotChannelNode : Node, IPilot
    {
        public Pilot Pilot { get; private set; }
        public Channel Channel { get; private set; }

        public event PilotClickDelegate OnPilotClick;
        public event PilotClickDelegate OnPilotChannelClick;

        public TextButtonNode ChannelNode { get; private set; }
        public TextButtonNode PilotNameNode { get; private set; }
        public TextNode ChannelChangeNode { get; private set; }

        private ColorNode channelColor;

        private EventManager eventManager;

        private ImageNode profileIcon;
        public bool UseTeamColor { get; set; }

        public PilotChannelNode(EventManager ev, ToolTexture background, Color hover, Color text, ToolTexture channelTexture)
        {
            ColorNode backgroundNode = new ColorNode(background);
            backgroundNode.StyleAsRow();
            AddChild(backgroundNode);

            eventManager = ev;

            profileIcon = new ImageNode("img/profileicon.png");
            profileIcon.Alpha = 0.3f;
            profileIcon.Visible = false;
            AddChild(profileIcon);

            PilotNameNode = new TextButtonNode("", Color.Transparent, hover, text);
            PilotNameNode.RelativeBounds = new RectangleF(0, 0, 0.83f, 1);
            PilotNameNode.TextNode.Alignment = RectangleAlignment.Center;

            PilotNameNode.OnClick += (mie) =>
            {
                OnPilotClick?.Invoke(mie, Pilot);
            };
            AddChild(PilotNameNode);

            ChannelNode = new TextButtonNode("", Color.Transparent, hover, text);
            ChannelNode.RelativeBounds = new RectangleF(0.7f, 0, 0.3f, 1);
            ChannelNode.OnClick += (mie) =>
            {
                OnPilotChannelClick?.Invoke(mie, Pilot);
            };
            ChannelNode.TextNode.Alignment = RectangleAlignment.BottomRight;
            AddChild(ChannelNode);

            channelColor = new ColorNode(channelTexture);
            channelColor.RelativeBounds = new RectangleF(0.65f, 0, 0.35f, 1);
            channelColor.Scale(0.6f);
            channelColor.StyleAsSwatch();
            ChannelNode.AddChild(channelColor);

            ChannelChangeNode = new TextNode("Δ", text);
            ChannelChangeNode.Visible = false;
            ChannelChangeNode.Style.Bold = true;
            ChannelChangeNode.Style.Border = true;
            channelColor.AddChild(ChannelChangeNode);

            RectangleF channelTextBounds = ChannelNode.TextNode.RelativeBounds;
            channelTextBounds.Width = channelColor.RelativeBounds.X - channelTextBounds.X;
            channelTextBounds.Width -= 0.075f;
            ChannelNode.TextNode.RelativeBounds = channelTextBounds;

            if (ShapeStyle.Modern)
            {
                ModernLayout();
            }
        }

        private bool modern;
        private ColorNode avatar;
        private TextNode initials;

        // The modern row: name on the left, the channel as a chip with a colour dot.
        private void ModernLayout()
        {
            modern = true;

            PilotNameNode.TextNode.Alignment = RectangleAlignment.CenterLeft;
            PilotNameNode.TextNode.RelativeBounds = new RectangleF(0.05f, 0.26f, 0.9f, 0.48f);
            PilotNameNode.RelativeBounds = new RectangleF(0, 0, 0.74f, 1);

            ChannelNode.RelativeBounds = new RectangleF(0.75f, 0.16f, 0.24f, 0.68f);
            ChannelNode.Background = new Color(255, 255, 255, 12);
            ChannelNode.BackgroundNode.CornerRadius = 7;
            ChannelNode.BackgroundNode.Inset = 0;
            ChannelNode.BackgroundNode.RingColor = ShapeStyle.Ring;
            ChannelNode.BackgroundNode.RingWidth = 1;
            ChannelNode.TextNode.Alignment = RectangleAlignment.CenterLeft;
            ChannelNode.TextNode.RelativeBounds = new RectangleF(0.38f, 0.16f, 0.58f, 0.68f);
            if (ShapeStyle.NumberFont != null)
            {
                ChannelNode.TextNode.Style.Font = ShapeStyle.NumberFont;
            }

            channelColor.RelativeBounds = new RectangleF(0.15f, 0.34f, 0.17f, 0.32f);
            channelColor.KeepAspectRatio = true;
            channelColor.CornerRadius = 3;
        }

        // Channel chip first, then the name (race rows in Rounds).
        public void ChannelFirst()
        {
            if (!modern)
                return;

            ChannelNode.RelativeBounds = new RectangleF(0.0f, 0.12f, 0.2f, 0.76f);
            PilotNameNode.RelativeBounds = new RectangleF(0.22f, 0, 0.78f, 1);
            PilotNameNode.TextNode.RelativeBounds = new RectangleF(0.02f, 0.18f, 0.96f, 0.64f);
        }

        // A round avatar with the pilot's initials in their channel colour (pilot list).
        public void ShowAvatar()
        {
            if (!modern || avatar != null)
                return;

            avatar = new ColorNode(Color.White);
            avatar.KeepAspectRatio = true;
            avatar.SetAspectRatio(1, 1);
            avatar.Alignment = RectangleAlignment.CenterLeft;
            avatar.CornerRadius = 100;
            avatar.RelativeBounds = new RectangleF(0.02f, 0.1f, 0.14f, 0.8f);
            AddChild(avatar, 1);

            initials = new TextNode("", new Color(3, 19, 24));
            initials.Style.Bold = true;
            initials.RelativeBounds = new RectangleF(0.14f, 0.28f, 0.72f, 0.44f);
            avatar.AddChild(initials);

            PilotNameNode.RelativeBounds = new RectangleF(0.16f, 0, 0.58f, 1);
            UpdateAvatar();
        }

        private void UpdateAvatar()
        {
            if (avatar == null)
                return;

            string name = Pilot != null ? Pilot.Name : "";
            initials.Text = name.Length >= 2 ? name.Substring(0, 2).ToUpper() : name.ToUpper();
            avatar.Visible = Pilot != null;

            Color c = channelColor.Tint;
            avatar.Color = Channel != null ? c : ShapeStyle.Elevated;
        }

        public void SetPilotChannel(PilotChannel pc)
        {
            if (pc != null)
            {
                SetPilotChannel(pc.Pilot, pc.Channel, null);
            }
            else
            {
                SetPilotChannel(null, null, null);
            }
        }

        public void UpdateProfileIcon()
        {
            if (Pilot == null)
            {
                profileIcon.Visible = false;
                return;
            }

            profileIcon.Visible = System.IO.File.Exists(IOTools.ResolveFromWorkingDirectory(Pilot.PhotoPath));

            if (modern)
            {
                profileIcon.Visible = false;
                return;
            }

            if (profileIcon.Visible)
            {
                profileIcon.RelativeBounds = new RectangleF(0.02f, 0.1f, 0.05f, 0.8f);
                PilotNameNode.RelativeBounds = new RectangleF(profileIcon.RelativeBounds.Right, 0, 0.83f - profileIcon.RelativeBounds.Right, 1);
            }
            else
            {
                PilotNameNode.RelativeBounds = new RectangleF(0, 0, 0.83f, 1);
            }
        }

        public void SetPilotChannel(Pilot p, Channel c, IEnumerable<Channel> shared)
        {
            Pilot = p;

            if (Pilot == null)
            {
                PilotNameNode.Text = "";
            }
            else
            {
                PilotNameNode.Text = Pilot.Name;
            }

            Channel = c;

            if (Channel == null)
            {
                ChannelNode.Text = "";
                channelColor.Tint = Theme.Current.PanelAlt.XNA;
            }
            else
            {
                ChannelNode.Text = Channel.UIDisplayName;

                if (Pilot == null && shared != null && shared.Any())
                {
                    ChannelNode.Text += "/" + string.Join("/",shared.Select(s => s.UIDisplayName));
                }

                if (UseTeamColor)
                {
                    channelColor.Tint = eventManager.GameManager.GetTeamColor(Channel);
                }
                else
                {
                    channelColor.Tint = eventManager.GetChannelColor(Channel);
                }
            }

            UpdateAvatar();
        }
    }

    public interface IPilot
    {
        Pilot Pilot { get; }
        Channel Channel { get; }
    }
}
