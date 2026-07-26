using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sound.AI;
using Xunit;
using Xunit.Abstractions;

namespace RaceLib.Tests
{
    /// <summary>
    /// Builds the COMPLETE core voice pack with a cloned voice — every number,
    /// digit and connecting word a race call needs — and leaves it on disk.
    ///
    /// This is the real thing rather than a sample: once it has run, every lap
    /// time from 0.00 to 99.99 can be spoken from local files with no network.
    /// Slow by design (hundreds of calls, paced against the rate limit), which
    /// is exactly why it belongs before a meeting and not during one.
    /// </summary>
    public class QwenFullPackLiveTests
    {
        private readonly ITestOutputHelper output;
        public QwenFullPackLiveTests(ITestOutputHelper output) { this.output = output; }

        [SkippableFact]
        public async Task BuildsTheFullCorePack()
        {
            string key = Environment.GetEnvironmentVariable("FPVTS_QWEN_KEY");
            string voice = Environment.GetEnvironmentVariable("FPVTS_QWEN_VOICE");
            string outDir = Environment.GetEnvironmentVariable("FPVTS_FULLPACK_OUT");
            Skip.IfNot(!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(voice) && !string.IsNullOrWhiteSpace(outDir),
                "set FPVTS_QWEN_KEY, FPVTS_QWEN_VOICE and FPVTS_FULLPACK_OUT to run");

            QwenTtsProvider provider = new QwenTtsProvider(key)
            {
                Style = "Speak as an energetic FPV drone-racing commentator: clear, punchy, easy to understand over a PA.",
            };

            string[] pilots = (Environment.GetEnvironmentVariable("FPVTS_PILOTS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();

            VoicePackGenerator gen = new VoicePackGenerator(provider, voice);
            gen.OnProgress += (i, total, frag) =>
            {
                if (i % 10 == 0 || i == total) output.WriteLine($"  {i}/{total}  {frag}");
            };
            gen.OnFragmentFailed += (frag, ex) => output.WriteLine($"  FAILED {frag}: {ex.Message}");

            VoicePackBuildResult result = await gen.BuildAsync(outDir, pilots, CancellationToken.None);
            output.WriteLine(result.ToString());

            Assert.NotNull(result.Pack);
            // A pack with holes cannot speak every time, which is the point.
            Assert.True(result.Failed <= 2, $"{result.Failed} fragments failed");

            // Every lap time across the range must now resolve locally.
            foreach (string t in new[] { "0.01", "5.55", "21.34", "47.09", "59.99", "99.98" })
            {
                string call = $"lap 1 in {t}";
                Assert.NotNull(result.Pack.Resolve(call));
            }
            output.WriteLine($"pack ready: {result.Pack.Count} fragments at {outDir}");
        }
    }
}
