using System.Collections.Generic;
using System.Linq;
using Xunit;
using RaceLib;

namespace RaceLib.Tests
{
    /// <summary>
    /// Tests for the optional per-seat channel carried in the "Paste Round" JSON
    /// (PastedPilot.Channel). When present and resolvable against the event's
    /// channels, the pilot is assigned to that exact channel; otherwise the paste
    /// falls back to FPVTrackside's auto-cycled channel-group assignment.
    /// The test event uses Channel.IMD6C: R1, R2, F2, F4, R7, R8.
    /// </summary>
    public class PasteChannelTests
    {
        [Fact]
        public void Pasted_Channel_Is_Assigned_To_Pilot()
        {
            using var bed = new EventTestBed();
            var king = bed.AddPilot("King");
            var little = bed.AddPilot("LittleFPV");
            var round = bed.CreateRound();

            string json = "[{\"externalRaceId\":1,\"pilots\":[" +
                "{\"name\":\"King\",\"channel\":\"R8\"}," +
                "{\"name\":\"LittleFPV\",\"channel\":\"F2\"}]}]";
            Assert.True(PastedRace.TryParsePastedRaces(json, out List<PastedRace> pasted));
            bed.EventManager.RoundManager.SetRoundPilots(round, pasted);

            var race = bed.RacesIn(round).First();
            Assert.Equal("R8", race.GetChannel(king).GetBandChannelText());
            Assert.Equal("F2", race.GetChannel(little).GetBandChannelText());
        }

        [Fact]
        public void Missing_Channel_Falls_Back_To_AutoAssign()
        {
            using var bed = new EventTestBed();
            var king = bed.AddPilot("King");
            var round = bed.CreateRound();

            // No channel field → pilot still gets assigned a channel (auto).
            string json = "[{\"externalRaceId\":1,\"pilots\":[{\"name\":\"King\"}]}]";
            Assert.True(PastedRace.TryParsePastedRaces(json, out List<PastedRace> pasted));
            bed.EventManager.RoundManager.SetRoundPilots(round, pasted);

            var race = bed.RacesIn(round).First();
            Assert.NotNull(race.GetChannel(king));
        }
    }
}
