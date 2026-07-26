using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tools;

namespace Sound.AI
{
    /// <summary>
    /// One entry point for the AI voice: build the pack, load it, and hand it
    /// to the speech system.
    ///
    /// The split that makes this work is between BUILD time and RACE time.
    /// Building talks to a cloud service and takes minutes; racing touches
    /// nothing but the local disk. So a race day needs no internet, no API key
    /// and no latency budget — the audio was made in advance and every call is
    /// a file open.
    /// </summary>
    public class AISpeechService
    {
        private readonly AISpeechSettings settings;
        private readonly string profileDirectory;

        public VoicePack Pack { get; private set; }
        public CommentaryLibrary Commentary { get; private set; }

        public AISpeechService(AISpeechSettings settings, string profileDirectory)
        {
            this.settings = settings;
            this.profileDirectory = profileDirectory;
        }

        /// <summary>
        /// Loads whatever has already been generated. Cheap and offline — safe
        /// to call on startup.
        /// </summary>
        public void Load()
        {
            if (settings == null || !settings.Enabled) return;

            Pack = VoicePack.Load(settings.PackDirectory(profileDirectory));
            Commentary = CommentaryLibrary.Load(settings.CommentaryDirectory(profileDirectory));
        }

        /// <summary>
        /// True when there is a usable pack. Checked before switching the
        /// speech system over, so a half-built pack never silences a race.
        /// </summary>
        public bool Ready => Pack != null && Pack.Count > 0;

        /// <summary>
        /// Points the speech system at the pack, if one is ready. Returns false
        /// when it is not, leaving the built-in voice in place — an unbuilt
        /// pack must degrade to the old behaviour, never to silence.
        /// </summary>
        public bool Attach(SpeechManager speech)
        {
            if (speech == null || !Ready) return false;

            speech.UseVoicePack(Pack);

            if (settings.CommentaryEnabled && Commentary != null && Commentary.Count > 0)
            {
                speech.Commentary = new CommentaryPlayer(Commentary)
                {
                    MinimumGap = TimeSpan.FromSeconds(Math.Max(5, settings.CommentaryEverySeconds)),
                };
            }
            return true;
        }

        /// <summary>
        /// Generates everything the given pilots need. Long-running and online;
        /// call it from a settings screen with a progress bar, never from the
        /// race loop.
        ///
        /// Only missing pieces are generated, so adding a pilot to an existing
        /// event costs one clip rather than a rebuild.
        /// </summary>
        public Task<VoicePackBuildResult> BuildAsync(
            IEnumerable<string> pilotNames,
            Action<int, int, string> progress,
            CancellationToken cancel)
        {
            return BuildAsync(pilotNames, null, progress, cancel);
        }

        /// <summary>
        /// soundTemplates are the actual TextToSpeech strings the event will
        /// speak. Passing them is what stops a call falling back to the system
        /// voice: a phrase the pack has never heard of cannot be assembled.
        /// </summary>
        public async Task<VoicePackBuildResult> BuildAsync(
            IEnumerable<string> pilotNames,
            IEnumerable<string> soundTemplates,
            Action<int, int, string> progress,
            CancellationToken cancel)
        {
            ITextToSpeechProvider provider = settings?.CreateProvider();
            if (provider == null)
                throw new InvalidOperationException("No API key configured for " + (settings?.Provider.ToString() ?? "the provider"));

            VoicePackGenerator generator = new VoicePackGenerator(provider, settings.VoiceId);
            if (progress != null) generator.OnProgress += (i, total, frag) => progress(i, total, frag);
            generator.OnFragmentFailed += (frag, ex) =>
                Logger.SoundLog?.Log(this, "VoicePack", "failed: " + frag + " — " + ex.Message, Logger.LogType.Error);

            VoicePackBuildResult result = await generator.BuildAsync(
                settings.PackDirectory(profileDirectory), pilotNames, soundTemplates, cancel);

            Pack = result.Pack;
            return result;
        }

        /// <summary>
        /// Writes and voices more commentary lines, adding to whatever is
        /// already there. Run it a few times across a season and the pool is
        /// deep enough that repeats are rare.
        /// </summary>
        public async Task<int> GrowCommentaryAsync(
            Action<int, int, string> progress,
            CancellationToken cancel)
        {
            ITextToSpeechProvider provider = settings?.CreateProvider();
            if (provider == null)
                throw new InvalidOperationException("No API key configured for speech");
            if (string.IsNullOrWhiteSpace(settings.CommentaryApiKey))
                throw new InvalidOperationException("Commentary needs a Gemini API key to write the lines");

            string dir = settings.CommentaryDirectory(profileDirectory);
            Directory.CreateDirectory(dir);

            CommentaryLibrary library = Commentary ?? CommentaryLibrary.Load(dir);
            CommentaryGenerator gen = new CommentaryGenerator(
                settings.CommentaryApiKey, provider, settings.VoiceId, settings.CommentaryModel)
            {
                Style = settings.CommentaryStyle,
            };
            if (progress != null) gen.OnProgress += (i, total, text) => progress(i, total, text);
            gen.OnLineFailed += (text, ex) =>
                Logger.SoundLog?.Log(this, "Commentary", "failed: " + text + " — " + ex.Message, Logger.LogType.Error);

            int added = await gen.GrowAsync(library, Math.Max(1, settings.CommentaryLinesPerBatch), cancel);
            Commentary = library;
            return added;
        }

        /// <summary>
        /// Every phrase the pack must cover for these sounds and pilots, so a
        /// settings screen can show what is missing BEFORE race day rather than
        /// discovering it mid-heat.
        /// </summary>
        public static IEnumerable<string> MissingFor(VoicePack pack, IEnumerable<string> pilotNames)
        {
            if (pack == null) yield break;

            foreach (KeyValuePair<string, string> f in VoicePackGenerator.RequiredFragments(pilotNames, null))
            {
                if (!pack.Has(f.Key)) yield return f.Key;
            }
        }
    }
}
