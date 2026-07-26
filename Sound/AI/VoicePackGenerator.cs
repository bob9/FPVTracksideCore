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

            // Finishing positions arrive as ABBREVIATIONS — "{position}"
            // renders as "1st", not "first" — so the clip is keyed on the
            // abbreviation and spoken as the word. Missing these is what sent
            // every lap call back to the system voice.
            for (int i = 1; i <= 32; i++)
            {
                yield return new KeyValuePair<string, string>(Ordinal(i), OrdinalWords(i));
            }

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

        /// <summary>"1st", "2nd", "3rd", "4th" — the form FPVTrackside substitutes.</summary>
        public static string Ordinal(int n)
        {
            int lastTwo = n % 100;
            if (lastTwo >= 11 && lastTwo <= 13) return n + "th";
            switch (n % 10)
            {
                case 1: return n + "st";
                case 2: return n + "nd";
                case 3: return n + "rd";
                default: return n + "th";
            }
        }

        /// <summary>The spoken form of an ordinal: "1st" is said "first".</summary>
        public static string OrdinalWords(int n)
        {
            string[] small =
            {
                "zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh",
                "eighth", "ninth", "tenth", "eleventh", "twelfth", "thirteenth", "fourteenth",
                "fifteenth", "sixteenth", "seventeenth", "eighteenth", "nineteenth", "twentieth"
            };
            if (n >= 0 && n < small.Length) return small[n];

            int tens = (n / 10) * 10, ones = n % 10;
            if (ones == 0) return NumberWords(tens).Replace("y", "ieth");
            return NumberWords(tens) + " " + small[ones];
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
        /// Splits a sound's text into the LITERAL phrases between its
        /// placeholders.
        ///
        /// "Arm your quads. Starting on the tone in less than {time}" yields
        /// one phrase; {time} is filled at race time from the number
        /// fragments. Generating that phrase as a single clip is what makes it
        /// sound like a sentence rather than a word-by-word announcement, and
        /// it is why the required set is derived from the actual sounds rather
        /// than a guessed word list — anything missing falls back to the system
        /// voice, which is exactly the wrong-voice symptom.
        /// </summary>
        public static IEnumerable<string> PhrasesFromTemplate(string template)
        {
            if (string.IsNullOrWhiteSpace(template)) yield break;

            System.Text.StringBuilder cur = new System.Text.StringBuilder();
            bool inPlaceholder = false;

            foreach (char c in template)
            {
                if (c == '{') { inPlaceholder = true; continue; }
                if (c == '}')
                {
                    inPlaceholder = false;
                    string done = Clean(cur.ToString());
                    if (done.Length > 0) yield return done;
                    cur.Clear();
                    continue;
                }
                if (!inPlaceholder) cur.Append(c);
            }

            string tail = Clean(cur.ToString());
            if (tail.Length > 0) yield return tail;
        }

        /// <summary>
        /// Trims a phrase to what should be SPOKEN. Sentence punctuation is
        /// dropped because it is not matched at resolution time, but the words
        /// are kept in order so the clip still reads naturally.
        /// </summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string[] words = VoicePack.Tokenise(s).ToArray();
            return string.Join(" ", words);
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
            return RequiredFragments(pilotNames, null);
        }

        /// <summary>
        /// Everything a meeting needs, including the literal phrases of every
        /// sound the event will actually speak. Passing the real templates is
        /// what stops a call falling back to the system voice.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> RequiredFragments(
            IEnumerable<string> pilotNames, IEnumerable<string> soundTemplates)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> f in CoreFragments())
            {
                if (seen.Add(VoicePack.Normalise(f.Key))) yield return f;
            }

            if (soundTemplates != null)
            {
                foreach (string template in soundTemplates)
                {
                    foreach (string phrase in PhrasesFromTemplate(template))
                    {
                        if (seen.Add(VoicePack.Normalise(phrase)))
                            yield return new KeyValuePair<string, string>(phrase, phrase);
                    }
                }
            }

            if (pilotNames == null) yield break;
            foreach (string name in pilotNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (seen.Add(VoicePack.Normalise(name)))
                    yield return new KeyValuePair<string, string>(name, name);
            }
        }

        /// <summary>
        /// Generates whatever is missing from the pack. Existing clips are left
        /// alone, so this is safe (and quick) to re-run when a pilot is added.
        /// </summary>
        public Task<VoicePackBuildResult> BuildAsync(
            string directory,
            IEnumerable<string> pilotNames,
            CancellationToken cancel)
        {
            return BuildAsync(directory, pilotNames, null, cancel);
        }

        public async Task<VoicePackBuildResult> BuildAsync(
            string directory,
            IEnumerable<string> pilotNames,
            IEnumerable<string> soundTemplates,
            CancellationToken cancel)
        {
            VoicePack pack = VoicePack.Load(directory) ?? new VoicePack(directory);
            pack.ProviderName = provider.Name;
            pack.VoiceId = voiceId;
            pack.VoiceName = provider.GetVoices().FirstOrDefault(v => v.Id == voiceId)?.Name ?? voiceId;

            KeyValuePair<string, string>[] required = RequiredFragments(pilotNames, soundTemplates).ToArray();
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
