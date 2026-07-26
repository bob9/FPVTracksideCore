using System.Linq;
using Sound.AI;
using Xunit;

namespace RaceLib.Tests
{
    /// <summary>
    /// The fragment scheme is what makes a lap call instant, so the rules that
    /// turn a call into fragments are worth pinning: get them wrong and either
    /// the call is silent or it falls back to slow cloud synthesis mid-race.
    /// </summary>
    public class VoicePackTests
    {
        /// <summary>
        /// A time is spoken the way a commentator says it — "twenty one, point,
        /// three, four" — not "twenty one point thirty four". Pre-generating
        /// every possible time would be ten thousand clips; this is why it is
        /// about a hundred.
        /// </summary>
        [Theory]
        [InlineData("21.34", new[] { "21", "point", "3", "4" })]
        [InlineData("9.07", new[] { "9", "point", "0", "7" })]
        [InlineData("100.5", new[] { "1", "hundred", "point", "5" })]
        [InlineData("3", new[] { "3" })]
        [InlineData("0.99", new[] { "0", "point", "9", "9" })]
        public void NumberSplitsIntoSpeakableFragments(string input, string[] expected)
        {
            Assert.Equal(expected, VoicePack.NumberToFragments(input).ToArray());
        }

        /// <summary>
        /// 0-99 are single clips because English speaks them as one unit; a
        /// composed "twenty" + "one" sounds like a robot reading digits.
        /// </summary>
        [Fact]
        public void WholeNumbersUnderOneHundredAreOneFragment()
        {
            for (int i = 0; i <= 99; i++)
            {
                Assert.Single(VoicePack.WholeNumberFragments(i));
            }
            Assert.Equal(new[] { "1", "hundred", "5" }, VoicePack.WholeNumberFragments(105).ToArray());
        }

        [Theory]
        [InlineData("bob9 lap 3 in 21.34", new[] { "bob9", "lap", "3", "in", "21", "point", "3", "4" })]
        [InlineData("Willman finished in 2", new[] { "willman", "finished", "in", "2" })]
        public void CallResolvesToFragments(string call, string[] expectedKeys)
        {
            VoicePack pack = BuildPack("bob9", "willman");
            string[] got = pack.Resolve(call);
            Assert.NotNull(got);
            Assert.Equal(expectedKeys, got.Select(VoicePack.Normalise).ToArray());
        }

        /// <summary>
        /// A call containing anything the pack cannot say must resolve to null
        /// so the caller speaks it live. Playing the parts it does have would
        /// drop a word — worse than a slow call.
        /// </summary>
        [Fact]
        public void UnknownWordFailsTheWholeCallRatherThanDroppingIt()
        {
            VoicePack pack = BuildPack("bob9");
            Assert.Null(pack.Resolve("bob9 lap 3 in 21.34 spectacular"));
            Assert.Null(pack.Resolve("unknownpilot lap 1 in 20.00"));
        }

        /// <summary>Punctuation and case must not stop a call resolving.</summary>
        [Fact]
        public void ResolutionToleratesPunctuationAndCase()
        {
            VoicePack pack = BuildPack("bob9");
            Assert.NotNull(pack.Resolve("BOB9, lap 3, in 21.34!"));
        }

        [Fact]
        public void TokeniserKeepsDecimalsWhole()
        {
            string[] tokens = VoicePack.Tokenise("bob9 lap 3 in 21.34").ToArray();
            Assert.Contains("21.34", tokens);
            Assert.DoesNotContain("21", tokens);
        }

        /// <summary>
        /// Callsigns can contain anything, so fragment filenames must be safe
        /// and must not collide between two different names.
        /// </summary>
        [Fact]
        public void FragmentFileNamesAreSafeAndDistinct()
        {
            string a = VoicePackGenerator.FileNameFor("bob/9");
            string b = VoicePackGenerator.FileNameFor("bob 9");
            string c = VoicePackGenerator.FileNameFor("bob9");

            Assert.DoesNotContain('/', a);
            Assert.NotEqual(a, b);
            Assert.NotEqual(b, c);
            Assert.EndsWith(".wav", a);
        }

        /// <summary>Numbers must read as words, or the clip says "two one".</summary>
        [Theory]
        [InlineData(0, "zero")]
        [InlineData(7, "seven")]
        [InlineData(13, "thirteen")]
        [InlineData(20, "twenty")]
        [InlineData(21, "twenty one")]
        [InlineData(99, "ninety nine")]
        public void NumbersAreGeneratedAsWords(int n, string expected)
        {
            Assert.Equal(expected, VoicePackGenerator.NumberWords(n));
        }

        /// <summary>
        /// The core set must cover every lap time to two decimals without a
        /// per-time clip — that ratio is the whole design.
        /// </summary>
        [Fact]
        public void CoreFragmentSetIsSmallButCoversEveryTime()
        {
            var core = VoicePackGenerator.CoreFragments().ToArray();
            Assert.InRange(core.Length, 100, 200);

            VoicePack pack = new VoicePack("/tmp/x");
            foreach (var f in core) pack.Add(f.Key, "/tmp/x/" + f.Key + ".wav");
            pack.Add("bob9", "/tmp/x/bob9.wav");

            // Spot-check across the range a race actually produces.
            foreach (string time in new[] { "0.01", "9.99", "21.34", "59.99", "99.98" })
            {
                Assert.NotNull(pack.Resolve("bob9 lap 1 in " + time));
            }
        }

        private static VoicePack BuildPack(params string[] pilots)
        {
            VoicePack pack = new VoicePack("/tmp/testpack");
            foreach (var f in VoicePackGenerator.CoreFragments())
            {
                pack.Add(f.Key, "/tmp/testpack/" + VoicePackGenerator.FileNameFor(f.Key));
            }
            foreach (string p in pilots)
            {
                pack.Add(p, "/tmp/testpack/" + VoicePackGenerator.FileNameFor(p));
            }
            return pack;
        }
    }
}
