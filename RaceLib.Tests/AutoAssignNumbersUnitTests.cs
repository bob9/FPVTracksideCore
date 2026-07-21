using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using RaceLib;

namespace RaceLib.Tests
{
    /// <summary>
    /// Focused tests for the root cause: a race flagged AutoAssignNumbers gets its
    /// number reset to (max number in the round) + 1 the next time a pilot is added to
    /// it. Races created by pasting must therefore NOT carry that flag.
    /// </summary>
    public class AutoAssignNumbersUnitTests
    {
        private static List<Tuple<Pilot, Channel>> TwoPilotHeats(EventTestBed bed, int heats)
        {
            Channel c0 = bed.EventManager.Channels[0];
            Channel c1 = bed.EventManager.Channels[1];
            var list = new List<Tuple<Pilot, Channel>>();
            for (int h = 0; h < heats; h++)
            {
                list.Add(Tuple.Create(bed.AddPilot($"P{h}A"), c0));
                list.Add(Tuple.Create(bed.AddPilot($"P{h}B"), c1));
            }
            return list;
        }

        [Fact]
        public void TextPaste_Numbers_Are_Contiguous_And_Not_AutoAssign()
        {
            using var bed = new EventTestBed();
            var round = bed.CreateRound();

            bed.EventManager.RoundManager.SetRoundPilots(round, TwoPilotHeats(bed, 3));

            Assert.Equal(new[] { 1, 2, 3 }, bed.RacesIn(round).Select(r => r.RaceNumber));
            Assert.All(bed.RacesIn(round), r => Assert.False(r.AutoAssignNumbers));
        }

        /// <summary>
        /// Adding a pilot to an opened race must not change that race's number.
        /// FAILS before the fix (the race jumps to max+1, leaving a gap).
        /// </summary>
        [Fact]
        public void Adding_Pilot_To_Opened_Pasted_Race_Does_Not_Renumber_It()
        {
            using var bed = new EventTestBed();
            var round = bed.CreateRound();
            bed.EventManager.RoundManager.SetRoundPilots(round, TwoPilotHeats(bed, 4)); // 1..4

            var rm = bed.EventManager.RaceManager;
            var second = rm.GetRaces(round).First(r => r.RaceNumber == 2);

            rm.SetRace(second);
            rm.AddPilot(rm.GetFreeChannel(second), bed.AddPilot("Extra"));

            Assert.Equal(2, second.RaceNumber);
            RaceNumberingInvariant.AssertContiguous(bed.EventManager, round);
        }
    }
}
