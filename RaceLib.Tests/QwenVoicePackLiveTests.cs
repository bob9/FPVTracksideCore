using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sound.AI;
using Xunit;

namespace RaceLib.Tests
{
    /// <summary>
    /// Live generation against Alibaba Model Studio using a CLONED voice — the
    /// club's own commentator — proving the whole path: cloud synthesis to
    /// local wav files that a race can then play with no network at all.
    ///
    /// Opt-in, because it costs API calls and needs a key:
    ///   FPVTS_QWEN_KEY=sk-... FPVTS_QWEN_VOICE=qwen-tts-vc-... dotnet test
    /// </summary>
    public class QwenVoicePackLiveTests
    {
        private static string Key => Environment.GetEnvironmentVariable("FPVTS_QWEN_KEY");
        private static string Voice => Environment.GetEnvironmentVariable("FPVTS_QWEN_VOICE");

        private static bool Enabled =>
            !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Voice);

        [SkippableFact]
        public async Task ClonedVoiceGeneratesPlayableLocalFiles()
        {
            Skip.IfNot(Enabled, "set FPVTS_QWEN_KEY and FPVTS_QWEN_VOICE to run");

            // The cloned voice must route to the cloning model — the flash
            // models reject a cloned id outright.
            Assert.True(QwenTtsProvider.IsClonedVoice(Voice), "voice id is not a cloned id");
            Assert.Equal(QwenTtsProvider.ClonedModel, QwenTtsProvider.ModelForVoice(Voice, true));

            string dir = Path.Combine(Path.GetTempPath(), "fpvts-qwen-pack-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                QwenTtsProvider provider = new QwenTtsProvider(Key)
                {
                    Style = "Speak as an energetic FPV drone-racing commentator: clear and punchy.",
                };

                // A representative slice rather than the whole pack: the pieces
                // a real lap call is assembled from.
                string[] phrases = { "21", "point", "3", "4", "lap", "bob9" };
                foreach (string phrase in phrases)
                {
                    string file = Path.Combine(dir, VoicePackGenerator.FileNameFor(phrase));
                    await provider.SynthesiseToFileAsync(
                        phrase == "21" ? "twenty one" : phrase, Voice, file, CancellationToken.None);

                    Assert.True(File.Exists(file), "no file written for " + phrase);

                    byte[] bytes = File.ReadAllBytes(file);
                    Assert.True(bytes.Length > 2000, $"{phrase}: only {bytes.Length} bytes — likely empty audio");

                    // It must be a real WAV the game can load, not a container
                    // the provider handed back verbatim.
                    Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
                    Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));

                    byte[] pcm = WavWriter.ExtractPcm(bytes, out int rate);
                    Assert.True(rate >= 16000, $"{phrase}: sample rate {rate} is too low");
                    Assert.True(pcm.Length > 1000, $"{phrase}: {pcm.Length} bytes of audio");
                    Assert.Equal(0, pcm.Length % 2); // 16-bit aligned

                    await Task.Delay(250); // pace against the rate limit
                }

                // The point of it all: those local files can now speak a lap
                // call with no network.
                VoicePack pack = new VoicePack(dir);
                foreach (string phrase in phrases)
                {
                    pack.Add(phrase, Path.Combine(dir, VoicePackGenerator.FileNameFor(phrase)));
                }
                string[] resolved = pack.ResolveFiles("bob9 lap 3 21.34");
                Assert.NotNull(resolved);
                Assert.All(resolved, f => Assert.True(File.Exists(f)));
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        [SkippableFact]
        public async Task ClonedVoicesAreDiscoverable()
        {
            Skip.IfNot(!string.IsNullOrWhiteSpace(Key), "set FPVTS_QWEN_KEY to run");

            QwenTtsProvider provider = new QwenTtsProvider(Key);
            var cloned = (await provider.GetClonedVoicesAsync(CancellationToken.None)).ToArray();

            // A club with a cloned voice must see it in the picker; without one
            // this simply returns empty rather than failing.
            foreach (TtsVoice v in cloned)
            {
                Assert.True(v.Cloned);
                Assert.StartsWith(QwenTtsProvider.ClonedVoicePrefix, v.Id);
                Assert.False(string.IsNullOrWhiteSpace(v.Name));
                Assert.DoesNotContain("-voice-", v.Name); // the enrolment name, not the raw id
            }
        }
    }
}
