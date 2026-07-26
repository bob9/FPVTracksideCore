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
    /// Builds the pack a specific profile actually needs: the core fragments,
    /// the literal phrases of every sound the event will speak, and the pilots.
    ///
    /// Deriving the phrases from the real Sounds.xml is the point. A guessed
    /// word list left "Arm your quads. Starting on the tone in less than" and
    /// the ordinal "1st" ungenerated, and every call containing them fell back
    /// to the system voice — which sounds exactly like the AI voice never took
    /// effect.
    /// </summary>
    public class QwenProfilePackLiveTests
    {
        private readonly ITestOutputHelper output;
        public QwenProfilePackLiveTests(ITestOutputHelper output) { this.output = output; }

        [SkippableFact]
        public async Task BuildsEverythingTheProfileSpeaks()
        {
            string key = Environment.GetEnvironmentVariable("FPVTS_QWEN_KEY");
            string voice = Environment.GetEnvironmentVariable("FPVTS_QWEN_VOICE");
            string outDir = Environment.GetEnvironmentVariable("FPVTS_PACK_DIR");
            string templateFile = Environment.GetEnvironmentVariable("FPVTS_TEMPLATES");
            Skip.IfNot(!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(voice)
                       && !string.IsNullOrWhiteSpace(outDir) && File.Exists(templateFile ?? ""),
                "set FPVTS_QWEN_KEY, FPVTS_QWEN_VOICE, FPVTS_PACK_DIR and FPVTS_TEMPLATES");

            string[] templates = File.ReadAllLines(templateFile)
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            string[] pilots = (Environment.GetEnvironmentVariable("FPVTS_PILOTS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();

            QwenTtsProvider provider = new QwenTtsProvider(key)
            {
                Style = "Speak as an energetic FPV drone-racing commentator: clear, punchy, easy to understand over a PA.",
            };

            VoicePackGenerator gen = new VoicePackGenerator(provider, voice);
            gen.OnProgress += (i, total, frag) =>
            {
                if (i % 10 == 0 || i == total) output.WriteLine($"  {i}/{total}  {frag}");
            };
            gen.OnFragmentFailed += (frag, ex) => output.WriteLine($"  FAILED {frag}: {ex.Message}");

            VoicePackBuildResult result = await gen.BuildAsync(outDir, pilots, templates, CancellationToken.None);
            output.WriteLine(result.ToString());

            // The calls that were falling back must now resolve locally.
            string[] mustSpeak =
            {
                "Arm your quads. Starting on the tone in less than 5",
                "Willman lap 1 in 1st",
                "bob9 lap 2 in 2nd",
                "Holeshot bob9 26.09",
                "60 seconds remaining",
                "Finish your lap and then land",
            };
            foreach (string call in mustSpeak)
            {
                string[] parts = result.Pack.Resolve(call);
                output.WriteLine(parts == null ? $"  STILL MISSING: {call}" : $"  ok ({parts.Length} clips): {call}");
                Assert.True(parts != null, "cannot speak locally: " + call);
            }
        }
    }
}
