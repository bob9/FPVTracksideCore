using System.Linq;
using Xunit;

namespace RaceLib.Tests
{
    /// <summary>
    /// HDZero low band (L1-L8) is an addition alongside D (Diatone) and L (LowBand),
    /// not a rename of either. It shares D band's frequencies so a Diatone-tuned
    /// RotorHazard node picks the HDZero pilot up, but keeps its own band type so a
    /// race can mix HDZero and analogue ground stations.
    /// </summary>
    public class HDZeroLowBandTests
    {
        [Fact]
        public void HDZeroLowBand_MatchesDiatoneFrequencies()
        {
            Assert.Equal(8, Channel.HDZeroLowBand.Length);
            for (int i = 0; i < 8; i++)
            {
                Channel z = Channel.HDZeroLowBand[i];
                Assert.Equal(Band.HDZero, z.Band);
                Assert.Equal('L', z.ChannelPrefix);
                Assert.Equal(i + 1, z.Number);
                Assert.Equal(Channel.Diatone[i].Frequency, z.Frequency);
                Assert.Equal(5362 + i * 37, z.Frequency);
                Assert.Equal("L" + (i + 1), z.GetBandChannelText());
                Assert.Equal("L" + (i + 1), z.DisplayName);
                Assert.Equal(BandType.HDZeroDigital, z.Band.GetBandType());
            }
        }

        [Fact]
        public void DAndLBandsAreUntouched()
        {
            Assert.Equal(new[] { 5333, 5373, 5413, 5453, 5493, 5533, 5573, 5613 }, Channel.LowBand.Select(c => c.Frequency));
            Assert.Equal(new[] { 5362, 5399, 5436, 5473, 5510, 5547, 5584, 5621 }, Channel.Diatone.Select(c => c.Frequency));
        }

        [Fact]
        public void HDZeroLowBand_IsInAllChannelsWithUniqueIds()
        {
            var all = Channel.AllChannelsUnmodified;
            Assert.Equal(all.Length, all.Select(c => c.ID).Distinct().Count());
            foreach (Channel z in Channel.HDZeroLowBand)
            {
                Assert.Contains(z, all);
                Assert.Same(z, Channel.GetChannel(Band.HDZero, z.Number, 'L'));
            }
        }
    }
}
