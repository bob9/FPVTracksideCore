using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using RaceLib;

namespace RaceLib.Tests
{
    /// <summary>
    /// Covers the "can't add laps during replay" fix. The replay "Add Lap Now" path
    /// (RaceManager.AddManualLapWithRace) must record a valid, countable lap even when the
    /// race being replayed has finished. A regression had widened the validity guard in
    /// AddLap so manual director-added laps in Race events were run through the live-timing
    /// checks (TimesUp etc.) and silently invalidated during replay.
    /// </summary>
    public class ReplayAddLapTests
    {
        private Race SetupFinishedRace(EventTestBed bed, Pilot pilot, PrimaryTimingSystemLocation timing)
        {
            bed.EventManager.Event.PrimaryTimingSystemLocation = timing;
            bed.EventManager.Event.Laps = 4;

            Round round = bed.CreateRound();
            Race race = bed.EventManager.RaceManager.AddRaceToRound(round);

            Channel channel = bed.EventManager.Channels.First();
            bed.EventManager.RaceManager.SetRacePilots(
                race,
                new[] { Tuple.Create(pilot, channel) },
                false);

            race.PrimaryTimingSystemLocation = timing;
            race.TargetLaps = 4;
            race.Start = DateTime.Now.AddMinutes(-5);
            race.End = race.Start.AddMinutes(2);

            // Replay sets the race as the current race (ShowReplay -> SetRace).
            bed.EventManager.RaceManager.SetRace(race);
            return race;
        }

        /// <summary>
        /// The replay "Add Lap Now" path records a valid, countable lap.
        /// (EndOfLap timing; in Holeshot timing the very first crossing is the holeshot by
        /// design, matching the live AddManualLap path.)
        /// </summary>
        [Fact]
        public void AddLapDuringReplay_RecordsACountableLap()
        {
            using var bed = new EventTestBed();
            Pilot pilot = bed.AddPilot("Maverick");
            Race race = SetupFinishedRace(bed, pilot, PrimaryTimingSystemLocation.EndOfLap);

            DateTime lapTime = race.Start.AddSeconds(30);
            bed.EventManager.RaceManager.AddManualLapWithRace(race, pilot, lapTime, 0);

            Lap[] realLaps = race.GetValidLaps(pilot, false);
            Assert.Single(realLaps);
            Assert.Equal(lapTime, realLaps[0].Detection.Time);
        }

        /// <summary>
        /// Adding a second lap during replay increments the countable lap count, in both
        /// timing modes.
        /// </summary>
        [Theory]
        [InlineData(PrimaryTimingSystemLocation.EndOfLap)]
        [InlineData(PrimaryTimingSystemLocation.Holeshot)]
        public void AddSecondLapDuringReplay_WhenPilotAlreadyHasLaps(PrimaryTimingSystemLocation timing)
        {
            using var bed = new EventTestBed();
            Pilot pilot = bed.AddPilot("Maverick");
            Race race = SetupFinishedRace(bed, pilot, timing);

            bed.EventManager.RaceManager.AddManualLapWithRace(race, pilot, race.Start.AddSeconds(30), 0);
            int before = race.GetValidLaps(pilot, false).Length;

            bed.EventManager.RaceManager.AddManualLapWithRace(race, pilot, race.Start.AddSeconds(60), 0);
            int after = race.GetValidLaps(pilot, false).Length;

            Assert.Equal(before + 1, after);
        }
    }
}
