using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Sound.AI
{
    /// <summary>
    /// A folder of pre-generated audio fragments for one voice, plus the rules
    /// for turning a race call into a list of those fragments.
    ///
    /// This is what makes a lap call instant. Cloud synthesis takes seconds —
    /// by the time a generated "bob9, lap three, twenty one thirty four" came
    /// back the pilot would be pilot most of the way round the next lap. So the
    /// call is not synthesised at race time at all: it is assembled from clips
    /// already on disk and played immediately.
    ///
    /// A time is spoken as its parts rather than as one clip per possible
    /// value: "21.34" is [twenty one][point][three][four]. Pre-generating every
    /// hundredth from 0.00 to 99.99 would be ten thousand clips; the whole
    /// numbers 0-99 plus ten digits and a handful of words cover every time in
    /// about a hundred and twenty.
    /// </summary>
    public class VoicePack
    {
        public const string IndexFileName = "voicepack.json";

        /// <summary>Root folder holding the fragment wavs.</summary>
        public string Directory { get; }

        /// <summary>Provider + voice this pack was generated with.</summary>
        public string ProviderName { get; set; }
        public string VoiceId { get; set; }
        public string VoiceName { get; set; }

        private readonly Dictionary<string, string> fragments =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public VoicePack(string directory)
        {
            Directory = directory;
        }

        public int Count => fragments.Count;

        public IEnumerable<KeyValuePair<string, string>> Fragments => fragments;

        /// <summary>Registers a fragment's audio file under a phrase key.</summary>
        public void Add(string key, string path)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            fragments[Normalise(key)] = path;
        }

        public bool Has(string key) => fragments.ContainsKey(Normalise(key));

        public string PathFor(string key)
        {
            return fragments.TryGetValue(Normalise(key), out string p) ? p : null;
        }

        /// <summary>
        /// Fragment keys are matched loosely so a phrase written slightly
        /// differently in the Sounds.xml still resolves.
        /// </summary>
        public static string Normalise(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '.') sb.Append(c);
                else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        // ------------------------------------------------------------------
        // Phrase -> fragment resolution
        // ------------------------------------------------------------------

        /// <summary>
        /// Splits a finished call into fragment keys, or returns null when any
        /// part is missing so the caller can fall back to live TTS rather than
        /// play a half-spoken line.
        ///
        /// Numbers are expanded into words here — the text arrives with the
        /// placeholders already substituted ("bob9 lap 3 in 21.34"), so this is
        /// where "21.34" becomes speakable parts.
        /// </summary>
        public string[] Resolve(string text)
        {
            string[] tokens = Tokenise(text).ToArray();
            List<string> keys = new List<string>();

            int i = 0;
            while (i < tokens.Length)
            {
                // Longest phrase first. A whole clip of "arm your quads starting
                // on the tone in less than" sounds like a sentence; the same
                // words played one at a time sound like a station announcement.
                int matched = MatchLongestPhrase(tokens, i, out string phraseKey);
                if (matched > 0)
                {
                    keys.Add(phraseKey);
                    i += matched;
                    continue;
                }

                string token = tokens[i];
                if (IsNumeric(token))
                {
                    foreach (string part in NumberToFragments(token))
                    {
                        if (!Has(part)) return null;
                        keys.Add(part);
                    }
                    i++;
                    continue;
                }

                if (Has(token))
                {
                    keys.Add(token);
                    i++;
                    continue;
                }
                return null; // an unknown word — better to speak it live than skip it
            }
            return keys.Count > 0 ? keys.ToArray() : null;
        }

        /// <summary>Longest run of tokens from <paramref name="start"/> that the pack holds as one clip.</summary>
        private int MatchLongestPhrase(string[] tokens, int start, out string key)
        {
            key = null;
            int best = 0;
            int max = Math.Min(MaxPhraseWords, tokens.Length - start);

            for (int len = max; len >= 2; len--)
            {
                string candidate = string.Join(" ", tokens, start, len);
                if (Has(candidate))
                {
                    key = Normalise(candidate);
                    best = len;
                    break;
                }
            }
            return best;
        }

        /// <summary>
        /// Longest phrase the pack will try to match. Bounded so resolution
        /// stays cheap: a call is looked up on every lap.
        /// </summary>
        public int MaxPhraseWords { get; set; } = 20;

        /// <summary>Resolves to actual file paths, or null if anything is missing.</summary>
        public string[] ResolveFiles(string text)
        {
            string[] keys = Resolve(text);
            if (keys == null) return null;

            string[] paths = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                paths[i] = PathFor(keys[i]);
                if (paths[i] == null || !File.Exists(paths[i])) return null;
            }
            return paths;
        }

        /// <summary>
        /// Splits text into words, keeping numbers (including decimals) whole
        /// and dropping punctuation that is not part of a number.
        /// </summary>
        public static IEnumerable<string> Tokenise(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;

            StringBuilder cur = new StringBuilder();
            foreach (char c in text)
            {
                bool partOfNumber = char.IsDigit(c) ||
                    (c == '.' && cur.Length > 0 && char.IsDigit(cur[cur.Length - 1]));

                if (char.IsLetterOrDigit(c) || partOfNumber)
                {
                    cur.Append(c);
                }
                else
                {
                    if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); }
                }
            }
            if (cur.Length > 0) yield return cur.ToString();
        }

        public static bool IsNumeric(string token)
        {
            return !string.IsNullOrEmpty(token) &&
                   double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                   char.IsDigit(token[0]);
        }

        /// <summary>
        /// Breaks a number into the fragments that speak it.
        ///
        /// A lap time reads as a commentator says it — "21.34" is "twenty one,
        /// point, three, four", not "twenty one point thirty four" — so the
        /// decimals are individual digits.
        /// </summary>
        public static IEnumerable<string> NumberToFragments(string token)
        {
            int dot = token.IndexOf('.');
            string whole = dot < 0 ? token : token.Substring(0, dot);
            string frac = dot < 0 ? "" : token.Substring(dot + 1);

            if (!int.TryParse(whole, out int w)) w = 0;

            foreach (string part in WholeNumberFragments(w)) yield return part;

            if (frac.Length > 0)
            {
                yield return "point";
                foreach (char d in frac) yield return d.ToString();
            }
        }

        /// <summary>
        /// Fragments for a whole number. 0-99 are single clips because English
        /// says them as one unit ("twenty one"); above that they are composed,
        /// which is fine for lap counts and rare for times.
        /// </summary>
        public static IEnumerable<string> WholeNumberFragments(int n)
        {
            if (n < 0) n = -n;
            if (n <= 99) { yield return n.ToString(CultureInfo.InvariantCulture); yield break; }

            if (n < 1000)
            {
                yield return (n / 100).ToString(CultureInfo.InvariantCulture);
                yield return "hundred";
                int rest = n % 100;
                if (rest > 0) yield return rest.ToString(CultureInfo.InvariantCulture);
                yield break;
            }
            // Beyond a thousand is not a race number; say the digits.
            foreach (char d in n.ToString(CultureInfo.InvariantCulture)) yield return d.ToString();
        }

        // ------------------------------------------------------------------
        // Persistence
        // ------------------------------------------------------------------

        public void Save()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var index = new
            {
                provider = ProviderName,
                voiceId = VoiceId,
                voiceName = VoiceName,
                fragments = fragments.ToDictionary(
                    kv => kv.Key,
                    kv => Path.GetFileName(kv.Value)),
            };
            File.WriteAllText(Path.Combine(Directory, IndexFileName),
                System.Text.Json.JsonSerializer.Serialize(index, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        public static VoicePack Load(string directory)
        {
            string indexPath = Path.Combine(directory, IndexFileName);
            if (!File.Exists(indexPath)) return null;

            VoicePack pack = new VoicePack(directory);
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(indexPath));
            System.Text.Json.JsonElement root = doc.RootElement;

            if (root.TryGetProperty("provider", out var p)) pack.ProviderName = p.GetString();
            if (root.TryGetProperty("voiceId", out var v)) pack.VoiceId = v.GetString();
            if (root.TryGetProperty("voiceName", out var vn)) pack.VoiceName = vn.GetString();

            if (root.TryGetProperty("fragments", out var frags))
            {
                foreach (System.Text.Json.JsonProperty f in frags.EnumerateObject())
                {
                    pack.Add(f.Name, Path.Combine(directory, f.Value.GetString()));
                }
            }
            return pack;
        }
    }
}
