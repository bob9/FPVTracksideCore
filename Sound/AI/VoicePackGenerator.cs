using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sound.AI
{
    /// <summary>
    /// Works out every fragment a meeting will need and synthesises the ones
    /// that are missing.
    ///
    /// Run before the meeting, not during it. Everything generated here is
    /// permanent: a fragment is only ever made once per voice, so the pack
    /// grows across meetings and later events cost almost nothing — a new
    /// pilot's name is a single clip on top of a pack that already exists.
    /// </summary>
    public class VoicePackGenerator
    {
        private readonly ITextToSpeechProvider provider;
        private readonly string voiceId;

        /// <summary>Fires per fragment so the UI can show progress.</summary>
        public event Action<int, int, string> OnProgress;

        /// <summary>Fires when one fragment fails; generation continues.</summary>
        public event Action<string, Exception> OnFragmentFailed;

        /// <summary>
        /// Pause between calls. Free tiers rate-limit aggressively and a pack is
        /// hundreds of fragments, so pacing is the difference between a pack
        /// that builds and one that dies half way.
        /// </summary>
        public TimeSpan Pace { get; set; } = TimeSpan.FromMilliseconds(250);

        public VoicePackGenerator(ITextToSpeechProvider provider, string voiceId)
        {
            this.provider = provider;
            this.voiceId = voiceId;
        }

        /// <summary>
        /// The fragments every race call is built from, independent of who is
        /// flying: numbers, digits and the connecting words.
        ///
        /// 0-99 are whole clips because English speaks them as one unit; that
        /// plus ten digits and "point" covers every lap time to two decimals,
        /// which would otherwise be ten thousand separate clips.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> CoreFragments()
        {
            for (int i = 0; i <= 99; i++)
            {
                yield return new KeyValuePair<string, string>(
                    i.ToString(CultureInfo.InvariantCulture), NumberWords(i));
            }
            yield return new KeyValuePair<string, string>("point", "point");
            yield return new KeyValuePair<string, string>("hundred", "hundred");

            string[] words =
            {
                "lap", "laps", "in", "seconds", "second", "finished", "position",
                "first", "second place", "third", "fourth", "fifth", "sixth",
                "seventh", "eighth", "holeshot", "sector", "and", "with",
                "fastest", "best", "personal best", "leads", "behind", "ahead",
                "of", "on", "for", "race", "round", "over", "go", "next up",
                "results", "time", "remaining", "up", "done",
            };
            foreach (string w in words)
            {
                yield return new KeyValuePair<string, string>(w, w);
            }
        }

        /// <summary>English words for 0-99, so the clip sounds like speech.</summary>
        public static string NumberWords(int n)
        {
            string[] ones =
            {
                "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
                "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
                "seventeen", "eighteen", "nineteen"
            };
            string[] tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

            if (n < 0) n = -n;
            if (n < 20) return ones[n];
            if (n < 100)
            {
                string t = tens[n / 10];
                int r = n % 10;
                return r == 0 ? t : t + " " + ones[r];
            }
            return n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Everything a meeting needs: the core fragments plus one clip per
        /// pilot name. Pilot names cannot be composed from anything, so each is
        /// its own fragment — and callsigns are exactly the words a generic TTS
        /// voice gets wrong, which is another reason to bake them once and
        /// check them rather than hope at race time.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> RequiredFragments(IEnumerable<string> pilotNames)
        {
            foreach (KeyValuePair<string, string> f in CoreFragments()) yield return f;

            if (pilotNames == null) yield break;
            foreach (string name in pilotNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                yield return new KeyValuePair<string, string>(name, name);
            }
        }

        /// <summary>
        /// Generates whatever is missing from the pack. Existing clips are left
        /// alone, so this is safe (and quick) to re-run when a pilot is added.
        /// </summary>
        public async Task<VoicePackBuildResult> BuildAsync(
            string directory,
            IEnumerable<string> pilotNames,
            CancellationToken cancel)
        {
            VoicePack pack = VoicePack.Load(directory) ?? new VoicePack(directory);
            pack.ProviderName = provider.Name;
            pack.VoiceId = voiceId;
            pack.VoiceName = provider.GetVoices().FirstOrDefault(v => v.Id == voiceId)?.Name ?? voiceId;

            KeyValuePair<string, string>[] required = RequiredFragments(pilotNames).ToArray();
            List<KeyValuePair<string, string>> missing = new List<KeyValuePair<string, string>>();

            foreach (KeyValuePair<string, string> f in required)
            {
                string existing = pack.PathFor(f.Key);
                if (existing != null && File.Exists(existing)) continue;
                missing.Add(f);
            }

            VoicePackBuildResult result = new VoicePackBuildResult
            {
                Total = required.Length,
                AlreadyPresent = required.Length - missing.Count,
            };

            System.IO.Directory.CreateDirectory(directory);

            for (int i = 0; i < missing.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                KeyValuePair<string, string> f = missing[i];
                string file = Path.Combine(directory, FileNameFor(f.Key));
                OnProgress?.Invoke(i + 1, missing.Count, f.Key);

                try
                {
                    await provider.SynthesiseToFileAsync(f.Value, voiceId, file, cancel);
                    pack.Add(f.Key, file);
                    result.Generated++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One bad fragment must not cost the whole pack — the rest
                    // are still worth having, and a re-run picks up the gap.
                    result.Failed++;
                    OnFragmentFailed?.Invoke(f.Key, ex);
                }

                if (Pace > TimeSpan.Zero && i < missing.Count - 1)
                {
                    await Task.Delay(Pace, cancel);
                }
            }

            pack.Save();
            result.Pack = pack;
            return result;
        }

        /// <summary>
        /// A filesystem-safe name for a fragment. Callsigns can contain
        /// anything, so unsafe characters are replaced and a short hash keeps
        /// two different names from colliding on the same file.
        /// </summary>
        public static string FileNameFor(string key)
        {
            string norm = VoicePack.Normalise(key);
            char[] safe = norm.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            string stem = new string(safe);
            if (stem.Length > 40) stem = stem.Substring(0, 40);

            uint hash = 2166136261;
            foreach (char c in norm) { hash = (hash ^ c) * 16777619; }
            return $"{stem}_{hash:x8}.wav";
        }
    }

    public class VoicePackBuildResult
    {
        public int Total { get; set; }
        public int AlreadyPresent { get; set; }
        public int Generated { get; set; }
        public int Failed { get; set; }
        public VoicePack Pack { get; set; }

        public override string ToString()
        {
            return $"{Generated} generated, {AlreadyPresent} already present, {Failed} failed, {Total} needed";
        }
    }
}
