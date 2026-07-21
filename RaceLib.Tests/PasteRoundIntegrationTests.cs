using System.Collections.Generic;
using System.Linq;
using Xunit;
using RaceLib;

namespace RaceLib.Tests
{
    /// <summary>
    /// End-to-end tests for pasting races into a round, driving the genuine paste
    /// pipeline used by the UI - PastedRace.TryParsePastedRaces(clipboardText) then
    /// RoundManager.SetRoundPilots(round, pastedRaces) - against a real EventManager
    /// backed by the JSON file database.
    /// </summary>
    public class PasteRoundIntegrationTests
    {
        // A four-heat clipboard, shaped exactly like the JSON the external bracket
        // system (cmrc.asn.au) puts on the clipboard.
        private const string ClipboardJson =
            "[{\"externalRaceId\":5278,\"pilots\":[{\"name\":\"King\",\"externalPilotId\":1},{\"name\":\"LittleFPV\",\"externalPilotId\":2}]}," +
            "{\"externalRaceId\":5279,\"pilots\":[{\"name\":\"JohnnyG\",\"externalPilotId\":3},{\"name\":\"Maccas\",\"externalPilotId\":4}]}," +
            "{\"externalRaceId\":5280,\"pilots\":[{\"name\":\"TommyB\",\"externalPilotId\":5},{\"name\":\"Spanna\",\"externalPilotId\":6}]}," +
            "{\"externalRaceId\":5281,\"pilots\":[{\"name\":\"Greedy\",\"externalPilotId\":7},{\"name\":\"Hitnstuff\",\"externalPilotId\":8}]}]";

        private static readonly string[] PilotNames =
            { "King", "LittleFPV", "JohnnyG", "Maccas", "TommyB", "Spanna", "Greedy", "Hitnstuff" };

        private static EventTestBed NewBedWithPilots()
        {
            var bed = new EventTestBed();
            foreach (var n in PilotNames) bed.AddPilot(n);
            return bed;
        }

        private static void Paste(EventTestBed bed, Round round)
        {
            Assert.True(PastedRace.TryParsePastedRaces(ClipboardJson, out List<PastedRace> pasted));
            bed.EventManager.RoundManager.SetRoundPilots(round, pasted);
        }

        [Fact]
        public void Paste_Into_Empty_Round_Numbers_1_To_4()
        {
            using var bed = NewBedWithPilots();
            var round = bed.CreateRound();

            Paste(bed, round);

            RaceNumberingInvariant.AssertContiguous(bed.EventManager, round);
            Assert.Equal(new[] { 1, 2, 3, 4 }, bed.RacesIn(round).Select(r => r.RaceNumber));
        }

        /// <summary>
        /// The reported bug, reproduced end to end.
        ///
        /// Create an empty round, paste -> races show as N-1..N-4. Then the user clicks
        /// a race to open it (SetRace makes it current) and a pilot is added as the race
        /// view fills. Because SetRoundPilots flagged the pasted races AutoAssignNumbers,
        /// AddPilot would renumber the opened race to max+1 (e.g. 9-1 -> 9-7), leaving a
        /// gap. After the fix the race keeps its number and the round stays contiguous.
        ///
        /// FAILS before the AutoAssignNumbers fix, passes after.
        /// </summary>
        [Fact]
        public void Opening_A_Pasted_Race_And_Adding_A_Pilot_Keeps_Its_Number()
        {
            using var bed = NewBedWithPilots();
            var round = bed.CreateRound();
            Paste(bed, round);

            var rm = bed.EventManager.RaceManager;
            var first = rm.GetRaces(round).First(r => r.RaceNumber == 1);

            // Click the race -> it becomes the current race.
            rm.SetRace(first);

            // A pilot is added to the now-current race (what happens as the race fills).
            var newcomer = bed.AddPilot("LateComer");
            rm.AddPilot(rm.GetFreeChannel(first), newcomer);

            Assert.Equal(1, first.RaceNumber);
            RaceNumberingInvariant.AssertContiguous(bed.EventManager, round);
        }

        [Fact]
        public void Pasted_Races_Are_Not_Flagged_AutoAssignNumbers()
        {
            using var bed = NewBedWithPilots();
            var round = bed.CreateRound();
            Paste(bed, round);

            Assert.All(bed.RacesIn(round), r => Assert.False(r.AutoAssignNumbers));
        }

        [Fact]
        public void Paste_Survives_Close_And_Reopen()
        {
            using var bed = NewBedWithPilots();
            var round = bed.CreateRound();
            Paste(bed, round);

            var roundId = round.ID;
            bed.Reload();

            var reloaded = bed.EventManager.RoundManager.Rounds.First(r => r.ID == roundId);
            RaceNumberingInvariant.AssertContiguous(bed.EventManager, reloaded);
            Assert.Equal(4, bed.RacesIn(reloaded).Length);
        }
    }
}
