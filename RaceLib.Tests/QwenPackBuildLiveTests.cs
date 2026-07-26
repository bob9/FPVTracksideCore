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
    /// Builds a real (small) voice pack end to end with the cloned voice and
    /// leaves it on disk, so the audio can be listened to. This is the whole
    /// feature in miniature: generate once, then speak a lap call from local
    /// files with no network.
    ///
    /// Opt-in and pointed at a directory you choose:
    ///   FPVTS_QWEN_KEY=... FPVTS_QWEN_VOICE=... FPVTS_PACK_OUT=/tmp/pack dotnet test
    /// </summary>
    public class QwenPackBuildLiveTests
    {
        private readonly ITestOutputHelper output;
        public QwenPackBuildLiveTests(ITestOutputHelper output) { this.output = output; }

        private static string Key => Environment.GetEnvironmentVariable("FPVTS_QWEN_KEY");
        private static string Voice => Environment.GetEnvironmentVariable("FPVTS_QWEN_VOICE");
        private static string Out => Environment.GetEnvironmentVariable("FPVTS_PACK_OUT");

        [SkippableFact]
        public async Task BuildsAPackAndSpeaksALapCallFromLocalFiles()
        {
            Skip.IfNot(!string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Voice) && !string.IsNullOrWhiteSpace(Out),
                "set FPVTS_QWEN_KEY, FPVTS_QWEN_VOICE and FPVTS_PACK_OUT to run");

            QwenTtsProvider provider = new QwenTtsProvider(Key)
            {
                Style = "Speak as an energetic FPV drone-racing commentator: clear, punchy, easy to understand over a PA.",
            };

            // A slice of the real pack — enough to speak a full lap call.
            string[] pilots = { "bob9", "Willman" };
            var wanted = new (string key, string say)[]
            {
                ("lap", "lap"), ("in", "in"), ("point", "point"),
                ("1", "one"), ("2", "two"), ("3", "three"), ("4", "four"),
                ("21", "twenty one"), ("33", "thirty three"),
                ("bob9", "bob9"), ("Willman", "Willman"),
            };

            VoicePack pack = VoicePack.Load(Out) ?? new VoicePack(Out);
            pack.ProviderName = provider.Name;
            pack.VoiceId = Voice;
            pack.VoiceName = QwenTtsProvider.ClonedVoiceName(Voice);
            Directory.CreateDirectory(Out);

            int made = 0;
            foreach ((string key, string say) in wanted)
            {
                string file = Path.Combine(Out, VoicePackGenerator.FileNameFor(key));
                if (File.Exists(file)) { pack.Add(key, file); continue; }

                await provider.SynthesiseToFileAsync(say, Voice, file, CancellationToken.None);
                pack.Add(key, file);
                made++;
                await Task.Delay(250); // pace against the rate limit
            }
            pack.Save();

            output.WriteLine($"pack at {Out}: {pack.Count} fragments ({made} generated this run)");

            // The payoff: a lap call resolves entirely to local files.
            string call = "bob9 lap 3 in 21.34";
            string[] files = pack.ResolveFiles(call);
            Assert.NotNull(files);
            output.WriteLine($"\"{call}\" plays as {files.Length} local clips:");
            foreach (string f in files) output.WriteLine("   " + Path.GetFileName(f));

            // Bake it into one clip so it can simply be listened to.
            string preview = Path.Combine(Out, "preview-lap-call.wav");
            WavWriter.Concatenate(preview, files, 40);
            Assert.True(new FileInfo(preview).Length > 5000);
            output.WriteLine("listen: " + preview);

            // And the index survives a reload, which is what a race start does.
            VoicePack reloaded = VoicePack.Load(Out);
            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded.ResolveFiles(call));
            Assert.Equal(QwenTtsProvider.ClonedVoiceName(Voice), reloaded.VoiceName);
        }
    }
}
