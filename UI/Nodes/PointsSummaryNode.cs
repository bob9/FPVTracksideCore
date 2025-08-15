﻿using Composition;
using Composition.Nodes;
using Microsoft.Xna.Framework;
using RaceLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;
using UI.Nodes.Rounds;

namespace UI.Nodes
{
    public class PointsSummaryNode : PilotSummaryTable
    {
        private bool brackets;

        public ResultManager PointsManager { get { return eventManager.ResultManager; } }

        public PointsSummaryNode(EventManager eventManager)
            :base(eventManager, "Points")
        {
        }

        public override void CreateHeadings(Node container, out Round[] rounds, out int column)
        {
            IEnumerable<Round> rs = eventManager.Event.Rounds.Where(r => r.EventType.HasPoints()).OrderBy(r => r.Order).ThenBy(r => r.RoundNumber);
            if (Round != null)
            {
                Round start = PointsManager.GetStartRound(Round);
                rs = rs.Where(r => r.Order >= start.Order && r.Order <= Round.Order);
            }
            column = 0;

            rounds = rs.ToArray();
            brackets = eventManager.RaceManager.GetRaces(r => rs.Contains(r.Round) && r.Bracket != Brackets.None).Any();
            if (brackets)
            {
                TextButtonNode brac = new TextButtonNode("Bracket", Theme.Current.InfoPanel.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.InfoPanel.Text.XNA);
                container.AddChild(brac);
                column++;
            }

            bool rollOver = rounds.Where(r => r.Stage != null && r.Stage.PointSummary != null).Any(r => r.Stage.PointSummary.RoundPositionRollover);

            bool prevTotal = false;
            bool anyTotal = false;

            foreach (Round r in rounds)
            {
                column++;
                int ca = column;

                if (prevTotal && rollOver)
                {
                    int ca3 = column;

                    TextButtonNode rro = new TextButtonNode("RRO", Theme.Current.InfoPanel.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.InfoPanel.Text.XNA);
                    rro.TextNode.Alignment = RectangleAlignment.TopRight;
                    rro.OnClick += (mie) => { columnToOrderBy = ca3; Refresh(); };
                    container.AddChild(rro);

                    prevTotal = false;
                    column++;
                }

                TextButtonNode headingText = new TextButtonNode(r.ToStringShort(), Theme.Current.InfoPanel.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.InfoPanel.Text.XNA);
                headingText.TextNode.Alignment = RectangleAlignment.TopRight;
                headingText.OnClick += (mie) => { columnToOrderBy = ca; Refresh(); };
                container.AddChild(headingText);

                if (r.Stage != null && r.Stage.PointSummary != null)
                {
                    column++;
                    int ca2 = column;

                    TextButtonNode sumText = new TextButtonNode("Total", Theme.Current.InfoPanel.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.InfoPanel.Text.XNA);
                    sumText.TextNode.Alignment = RectangleAlignment.TopRight;
                    sumText.OnClick += (mie) => { columnToOrderBy = ca2; Refresh(); };
                    container.AddChild(sumText);
                    prevTotal = true;
                    anyTotal = true;
                }
            }

            if (!anyTotal)
            {
                int col = column + 1;

                TextButtonNode sumText = new TextButtonNode("Total", Theme.Current.InfoPanel.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.InfoPanel.Text.XNA);
                sumText.TextNode.Alignment = RectangleAlignment.TopRight;
                sumText.OnClick += (mie) => { columnToOrderBy = col; Refresh(); };
                container.AddChild(sumText);
                prevTotal = true;
                anyTotal = true;
            }
        }

        public override IEnumerable<Round> GetSummaryRounds()
        {
            return eventManager.Event.Rounds.Where(r => r.EventType.HasPoints() && r.Stage != null && r.Stage.PointSummary != null).OrderBy(r => r.Order);
        }

        public override void SetOrder()
        {
            IEnumerable<Node> ordered;

            // order them
            if (columnToOrderBy == 0)
            {
                ordered = rows.ChildrenOfType.OrderBy(pa => pa.Bracket).ThenBy(pa => pa.Pilot.Name);
            }
            else
            {
                ordered = rows.ChildrenOfType.OrderBy(pa => pa.Bracket).ThenByDescending(pa => pa.GetValue(columnToOrderBy));
            }

            rows.SetOrder(ordered);
        }

        protected override void SetResult(PilotResultNode pilotResNode, Pilot pilot, Round[] rounds)
        {
            bool prevTotal = false;
            bool anyTotal = false;

            List<Node> nodes = new List<Node>();

            bool rollOver = rounds.Where(r => r.Stage != null && r.Stage.PointSummary != null).Any(r => r.Stage.PointSummary.RoundPositionRollover);

            Brackets bracket = Brackets.None;

            foreach (Round round in rounds)
            {
                if (prevTotal && rollOver)
                {
                    Round rolloverRound = eventManager.ResultManager.GetRollOverRound(round);
                    if (rolloverRound != null)
                    {
                        Result res = eventManager.ResultManager.GetRollOver(pilot, rolloverRound, false);

                        ResultNode tn = new ResultNode(res);
                        tn.Alignment = RectangleAlignment.CenterRight;
                        nodes.Add(tn);
                        prevTotal = false;
                    }
                }

                Result r = PointsManager.GetResult(round, pilot);
                ResultNode rn = new ResultNode(r);
                nodes.Add(rn);

                if (r != null && r.Race.Bracket != Brackets.None && brackets)
                {
                    bracket = r.Race.Bracket;
                }

                if (round.Stage != null && round.Stage.PointSummary != null)
                {
                    prevTotal = true;
                    int points = PointsManager.GetPointsTotal(round, pilot);
                    TextNode tn = new TextNode(points.ToString(), Theme.Current.Rounds.Text.XNA);
                    tn.Alignment = RectangleAlignment.CenterRight;
                    tn.Style.Bold = true;
                    nodes.Add(tn);
                    anyTotal = true;
                }
            }

            if (!anyTotal)
            {
                Round last = rounds.LastOrDefault();

                if (last != null)
                {
                    int points = PointsManager.GetPointsTotal(last, pilot);
                    TextNode tn = new TextNode(points.ToString(), Theme.Current.Rounds.Text.XNA);
                    tn.Alignment = RectangleAlignment.CenterRight;
                    tn.Style.Bold = true;
                    nodes.Add(tn);
                }
            }

            pilotResNode.Set(pilot, bracket, nodes);
        }
    }
}
