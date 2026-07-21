using System;
using System.Linq;
using Xunit;
using RaceLib;

namespace RaceLib.Tests
{
    /// <summary>
    /// Covers the immediate split-detection flush. Split (sector) crossings used
    /// to reach the race's JSON file only as a side effect of the NEXT lap-end
    /// write (db.Insert on a Detection is a no-op in the JSON database — the
    /// detection persists embedded in Race.json). External consumers polling
    /// Race.json (the dd-pits venue agent feeding the AUFPV platform's live 3D
    /// track view) therefore saw a pilot's sector crossings up to a whole lap
    /// late. RaceManager.OnDetection's split branch and AddManualSector now call
    /// db.Update(currentRace) after adding the detection — the same pattern
    /// Race.RecordLap uses for laps.
    ///
    /// The test proves persistence by reloading the event from disk: without the
    /// flush the split detection lives only in memory and a reload loses it.
    /// </summary>
    public class SplitFlushTests
    {
        [Fact]
        public void ManualSector_IsFlushedToDiskImmediately()
        {
            using var bed = new EventTestBed();
            Pilot pilot = bed.AddPilot("Maverick");

            bed.EventManager.Event.PrimaryTimingSystemLocation = PrimaryTimingSystemLocation.EndOfLap;
            bed.EventManager.Event.Laps = 4;

            Round round = bed.CreateRound();
            Race race = bed.EventManager.RaceManager.AddRaceToRound(round);
            Channel channel = bed.EventManager.Channels.First();
            bed.EventManager.RaceManager.SetRacePilots(
                race,
                new[] { Tuple.Create(pilot, channel) },
                false);
            race.TargetLaps = 4;
            race.Start = DateTime.Now.AddMinutes(-1);
            bed.EventManager.RaceManager.SetRace(race);

            Guid raceId = race.ID;
            DateTime crossing = race.Start.AddSeconds(12);

            // A split-timer crossing (timing system index 2 = second split gate).
            bed.EventManager.RaceManager.AddManualSector(pilot, crossing, 2);

            // Sanity: it landed in memory as a non-lap-end detection.
            Assert.Contains(race.Detections, d => !d.IsLapEnd && d.TimingSystemIndex == 2);

            // The real assertion: close and re-open the event from disk. No lap
            // was ever recorded, so nothing else could have flushed the race —
            // if the split survives the reload, it was written immediately.
            bed.Reload();

            Race reloaded = bed.EventManager.RaceManager.Races.FirstOrDefault(r => r.ID == raceId);
            Assert.NotNull(reloaded);
            Detection persisted = reloaded.Detections.FirstOrDefault(d => !d.IsLapEnd && d.TimingSystemIndex == 2);
            Assert.NotNull(persisted);
            Assert.Equal(pilot.ID, persisted.Pilot.ID);
        }
    }
}
