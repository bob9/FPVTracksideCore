using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Composition;
using Composition.Nodes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using RaceLib;
using Tools;
using UI.Video;

namespace UI.Nodes
{
    public class TopBarNode : AnimatedNode
    {
        public EventManager EventManager { get; private set; }

        private TextNode eventName;
        private EventTypeSummaryNode type;

        private TextNode currentTime;
        private LapSummaryNode lapSummaryNode;
        private RaceTimeNode timeNode;
        private RaceTimeRemainingNode remainingNode;

        private int lastMinute;

        private Node lower;
        private Node upper;
        public AlphaAnimatedNode TabContainer { get; private set; }

        private AnimatedRelativeNode logoContainerNode;

        // With a rounded theme each race figure sits on its own chip.
        private Dictionary<Node, Node> chips = new Dictionary<Node, Node>();
        private Node container;

        public TopBarNode()
        {
            lastMinute = -1;

            container = new ColorNode(Theme.Current.TopPanel);
            container.RelativeBounds = new RectangleF(0, 0, 1, 0.76f);
            AddChild(container);

            logoContainerNode = new AnimatedRelativeNode();
            logoContainerNode.RelativeBounds = new RectangleF(0, 0, 0.3f, 1f);
            container.AddChild(logoContainerNode);

            ImageNode logoNode = new ColorNode(Theme.Current.FPVTracksideLogo);
            logoNode.Alignment = RectangleAlignment.CenterLeft;
            logoNode.KeepAspectRatio = true;
            logoContainerNode.AddChild(logoNode);

            float height = 0.5f;
            upper = new Node();
            upper.RelativeBounds = new RectangleF(logoContainerNode.RelativeBounds.Right, 0, 1 - logoContainerNode.RelativeBounds.Right, height);
            upper.Scale(0.98f, 0.95f);
            
            lower = new Node();
            lower.RelativeBounds = new RectangleF(logoContainerNode.RelativeBounds.Right, height, 1 - logoContainerNode.RelativeBounds.Right, height);
            lower.Scale(0.98f, 0.95f);

            TabContainer = new AlphaAnimatedNode();
            TabContainer.RelativeBounds = new RectangleF(0, container.RelativeBounds.Bottom, 1, 1 - container.RelativeBounds.Bottom);

            container.AddChild(upper);
            container.AddChild(lower);
            AddChild(TabContainer);

            LogoOnBottomLine(false);
        }

        public void Init(EventManager eventManager, ReplayNode replayNode)
        {
            EventManager = eventManager;

            bool border = Theme.Current.TopPanelTextBorder;

            eventName = new TextNode("", Theme.Current.TopPanelText.XNA);
            eventName.Alignment = RectangleAlignment.CenterLeft;
            eventName.RelativeBounds = new RectangleF(0, 0, 0.85f, 1f);
            eventName.Style.Border = border;
            upper.AddChild(eventName);

            currentTime = new TextNode("", Theme.Current.TopPanelText.XNA);
            currentTime.Alignment = RectangleAlignment.CenterRight;
            currentTime.RelativeBounds = new RectangleF(eventName.RelativeBounds.Right, 0, 1 - eventName.RelativeBounds.Right, 1f);
            currentTime.Style.Border = border;
            upper.AddChild(currentTime);

            type = new EventTypeSummaryNode(EventManager, Theme.Current.TopPanelText.XNA);
            type.Style.Border = border;
            type.AddChild(new HoverNode(Theme.Current.Hover.XNA));
            AddFigure(type);

            lapSummaryNode = new LapSummaryNode(eventManager, replayNode, Theme.Current.TopPanelText.XNA);
            lapSummaryNode.Alignment = RectangleAlignment.CenterLeft;
            lapSummaryNode.Style.Border = border;
            lapSummaryNode.AddChild(new HoverNode(Theme.Current.Hover.XNA));
            AddFigure(lapSummaryNode);

            timeNode = new RaceTimeNode(eventManager.RaceManager, replayNode, Theme.Current.TopPanelText.XNA);
            timeNode.Alignment = RectangleAlignment.CenterLeft;
            timeNode.Style.Border = border;
            AddFigure(timeNode);

            remainingNode = new RaceTimeRemainingNode(eventManager.RaceManager, replayNode, Theme.Current.TopPanelText.XNA);
            remainingNode.Alignment = RectangleAlignment.CenterLeft;
            remainingNode.Style.Border = border;
            AddFigure(remainingNode);

            if (ShapeStyle.Modern)
            {
                BuildModern();
            }

            LogoOnBottomLine(false);

            RepositionNodes();

            EventManager.OnEventChange += UpdateDetails;
            EventManager.RaceManager.OnRaceClear += UpdateDetails;
            EventManager.RaceManager.OnRaceChanged += UpdateDetails;
            EventManager.RaceManager.OnRaceReset += UpdateDetails;

            UpdateDetails();
            SetAnimationTime(TimeSpan.FromSeconds(ApplicationProfileSettings.Instance.ReOrderAnimationSeconds));
        }

        private void AddFigure(TextNode node)
        {
            if (ShapeStyle.Modern)
            {
                container.AddChild(node);
                return;
            }

            if (ShapeStyle.ChipRadius <= 0)
            {
                lower.AddChild(node);
                return;
            }

            ColorNode chip = new ColorNode(Theme.Current.PanelAlt.XNA);
            chip.StyleAsChip();
            chip.Inset = 3;
            chip.RingColor = ShapeStyle.Ring;
            chip.RingWidth = ShapeStyle.RingWidth;
            lower.AddChild(chip);

            node.Alignment = RectangleAlignment.Center;
            node.RelativeBounds = new RectangleF(0.06f, 0.2f, 0.88f, 0.6f);
            chip.AddChild(node);
            chips[node] = chip;
        }

        // What is laid out in the bar for a figure: its chip, or the figure itself.
        private Node Holder(Node node)
        {
            Node chip;
            if (chips.TryGetValue(node, out chip))
                return chip;
            return node;
        }

        public void DisableTimeNodes()
        {
            Holder(remainingNode).Visible = false;
            Holder(timeNode).Visible = false;
        }


        public override void Dispose()
        {
            EventManager.OnEventChange -= UpdateDetails;
            EventManager.RaceManager.OnRaceClear -= UpdateDetails;
            EventManager.RaceManager.OnRaceChanged -= UpdateDetails;
            EventManager.RaceManager.OnRaceReset -= UpdateDetails;

            base.Dispose();
        }

        private RaceStatusNode statusNode;
        private TextNode timeLabel;
        private TextNode remainingLabel;

        // The modern header: logo, event, tabs, race state and timers on one row.
        private void BuildModern()
        {
            container.RelativeBounds = new RectangleF(0, 0, 1, 1);
            container.Remove();
            AddChild(container, 0);

            TabContainer.Remove();
            container.AddChild(TabContainer);

            upper.Visible = false;
            lower.Visible = false;

            ColorNode divider = new ColorNode(new Color(148, 163, 184, 46));
            divider.RelativeBounds = new RectangleF(0.1265f, 0.3f, 0.0006f, 0.4f);
            container.AddChild(divider);

            eventName.Remove();
            container.AddChild(eventName);
            eventName.Style.Bold = true;

            type.Tint = ShapeStyle.Muted;
            type.Alignment = RectangleAlignment.CenterLeft;

            lapSummaryNode.Alignment = RectangleAlignment.Center;

            statusNode = new RaceStatusNode(EventManager.RaceManager);
            container.AddChild(statusNode);

            timeLabel = Label(Translator.Get("Label.RaceTime", "RACE TIME"));
            remainingLabel = Label(Translator.Get("Label.Remaining", "REMAINING"));

            foreach (RaceTimeNode figure in new RaceTimeNode[] { timeNode, remainingNode })
            {
                figure.Prepend = "";
                figure.Alignment = RectangleAlignment.CenterRight;
                if (ShapeStyle.NumberFont != null)
                {
                    figure.Style.Font = ShapeStyle.NumberFont;
                }
            }
            timeNode.Tint = ShapeStyle.Primary.A > 0 ? ShapeStyle.Primary : Color.White;

            currentTime.Remove();
            container.AddChild(currentTime);
            currentTime.Tint = ShapeStyle.Muted;
            if (ShapeStyle.NumberFont != null)
            {
                currentTime.Style.Font = ShapeStyle.NumberFont;
            }
        }

        private TextNode Label(string text)
        {
            TextNode label = new TextNode(text, ShapeStyle.Muted);
            label.Alignment = RectangleAlignment.BottomRight;
            label.Style.Bold = true;
            container.AddChild(label);
            return label;
        }

        private void RepositionModern()
        {
            eventName.Visible = ApplicationProfileSettings.Instance.TopEventName;
            type.Visible = ApplicationProfileSettings.Instance.TopEventType;
            lapSummaryNode.Visible = ApplicationProfileSettings.Instance.TopLapInfo;
            timeNode.Visible = timeLabel.Visible = ApplicationProfileSettings.Instance.TopRaceTime;
            remainingNode.Visible = remainingLabel.Visible = ApplicationProfileSettings.Instance.TopRemainingTime;
            currentTime.Visible = ApplicationProfileSettings.Instance.TopClock;

            logoContainerNode.RelativeBounds = new RectangleF(0.01f, 0.22f, 0.11f, 0.56f);

            eventName.RelativeBounds = new RectangleF(0.135f, 0.17f, 0.15f, 0.36f);
            type.RelativeBounds = new RectangleF(0.135f, 0.56f, 0.15f, 0.26f);

            TabContainer.RelativeBounds = new RectangleF(0.27f, 0.16f, 0.43f, 0.68f);

            statusNode.RelativeBounds = new RectangleF(0.708f, 0.27f, 0.062f, 0.46f);
            lapSummaryNode.RelativeBounds = new RectangleF(0.772f, 0.34f, 0.066f, 0.32f);

            timeLabel.RelativeBounds = new RectangleF(0.838f, 0.16f, 0.05f, 0.18f);
            timeNode.RelativeBounds = new RectangleF(0.838f, 0.38f, 0.05f, 0.44f);
            remainingLabel.RelativeBounds = new RectangleF(0.893f, 0.16f, 0.05f, 0.18f);
            remainingNode.RelativeBounds = new RectangleF(0.893f, 0.38f, 0.05f, 0.44f);

            currentTime.RelativeBounds = new RectangleF(0.948f, 0.38f, 0.046f, 0.26f);
            RequestLayout();
        }

        public void RepositionNodes()
        {
            if (statusNode != null)
            {
                RepositionModern();
                return;
            }

            float top = 0.075f;
            float height = 1 - (2 * top);

            eventName.Visible = ApplicationProfileSettings.Instance.TopEventName;
            Holder(type).Visible = ApplicationProfileSettings.Instance.TopEventType;
            Holder(lapSummaryNode).Visible = ApplicationProfileSettings.Instance.TopLapInfo;
            Holder(timeNode).Visible = ApplicationProfileSettings.Instance.TopRaceTime;
            Holder(remainingNode).Visible = ApplicationProfileSettings.Instance.TopRemainingTime;
            currentTime.Visible = ApplicationProfileSettings.Instance.TopClock;

            Holder(type).RelativeBounds = new RectangleF(0, top, 0.37f, height);
            Holder(lapSummaryNode).RelativeBounds = new RectangleF(0, top, 0.18f, height);
            Holder(timeNode).RelativeBounds = new RectangleF(0, top, 0.19f, height);
            Holder(remainingNode).RelativeBounds = new RectangleF(0, top, 0.26f, height);

            Node[] ordered = new Node[] { Holder(type), Holder(lapSummaryNode), Holder(timeNode), Holder(remainingNode) };

            float current = 0f;
            foreach (Node node in ordered)
            {
                if (node.Visible)
                {
                    RectangleF bounds = node.RelativeBounds;
                    bounds.X = current;
                    current = bounds.Right;

                    node.RelativeBounds = bounds;
                }
            }
            RequestLayout();
        }


        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            DateTime now = DateTime.Now;

            if (now.Minute != lastMinute)
            {
                currentTime.Text = now.ToString("h:mm tt").ToLower();
                lastMinute = now.Minute;
            }
        }

        public void UpdateDetails(object o)
        {
            UpdateDetails();
        }

        public void UpdateDetails()
        {
            if (EventManager.Event != null)
            {
                eventName.Text = EventManager.Event.Name;
            }

            lapSummaryNode.NeedsUpdate = true;
        }

        public void LogoOnBottomLine(bool bottom)
        {
            if (statusNode != null)
            {
                // The modern header keeps its layout; only the tabs come and go.
                TabContainer.SetAnimatedVisibility(!bottom);
                return;
            }

            if (bottom)
            {
                TabContainer.SetAnimatedVisibility(false);
                logoContainerNode.RelativeBounds = new RectangleF(0, 0.5f, 0.3f, 0.5f);
            }
            else
            {
                logoContainerNode.RelativeBounds = new RectangleF(0, 0, 0.3f, 1f);
                TabContainer.SetAnimatedVisibility(true);
            }
        }

        public override void SetAnimationTime(TimeSpan time)
        {
            logoContainerNode.SetAnimationTime(time);
            TabContainer.SetAnimationTime(AnimationTime);
            base.SetAnimationTime(time);
        }
    }
}
