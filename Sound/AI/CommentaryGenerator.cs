using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sound.AI
{
    /// <summary>
    /// Writes new commentary lines with a language model and voices them into
    /// the library, ahead of the meeting.
    ///
    /// The lines must be reusable, so they are about racing in general rather
    /// than a particular lap — a line quoting a time could only ever be played
    /// once, and anything needing live numbers is a race call anyway. Each run
    /// adds to the library rather than replacing it, so the pool deepens over
    /// time and a club that has run several meetings rarely hears a repeat.
    /// </summary>
    public class CommentaryGenerator
    {
        private readonly string apiKey;
        private readonly string model;
        private readonly ITextToSpeechProvider tts;
        private readonly string voiceId;
        private readonly HttpClient http;

        public event Action<int, int, string> OnProgress;
        public event Action<string, Exception> OnLineFailed;

        /// <summary>
        /// A fast model with thinking disabled. Writing a handful of one-line
        /// calls is not a reasoning task, and thinking models spend far longer
        /// deliberating than writing — irrelevant to correctness here, but it
        /// makes generating a large batch needlessly slow.
        /// </summary>
        public const string DefaultModel = "gemini-3.1-flash-lite";

        public CommentaryGenerator(string apiKey, ITextToSpeechProvider tts, string voiceId,
            string model = null, HttpClient client = null)
        {
            this.apiKey = (apiKey ?? "").Trim();
            this.tts = tts;
            this.voiceId = voiceId;
            this.model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
            http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        }

        /// <summary>Optional club/voice flavour, e.g. "dry Australian humour".</summary>
        public string Style { get; set; }

        /// <summary>
        /// Generates <paramref name="perMoment"/> new lines for each moment and
        /// voices them into the library. Lines already present are skipped, so
        /// running it repeatedly grows the pool.
        /// </summary>
        public async Task<int> GrowAsync(CommentaryLibrary library, int perMoment, CancellationToken cancel)
        {
            CommentaryMoment[] moments = (CommentaryMoment[])Enum.GetValues(typeof(CommentaryMoment));
            int added = 0;
            int step = 0, steps = moments.Length * perMoment;

            foreach (CommentaryMoment moment in moments)
            {
                cancel.ThrowIfCancellationRequested();

                string[] texts;
                try
                {
                    texts = await WriteLinesAsync(moment, perMoment, library, cancel);
                }
                catch (Exception ex)
                {
                    OnLineFailed?.Invoke(moment.ToString(), ex);
                    continue;
                }

                foreach (string text in texts)
                {
                    cancel.ThrowIfCancellationRequested();
                    step++;
                    if (string.IsNullOrWhiteSpace(text) || library.HasText(text)) continue;

                    OnProgress?.Invoke(step, steps, text);
                    string file = Path.Combine(library.Directory,
                        $"c_{moment.ToString().ToLowerInvariant()}_{Guid.NewGuid():N}.wav".Replace("-", ""));

                    try
                    {
                        await tts.SynthesiseToFileAsync(text, voiceId, file, cancel);
                        library.Add(new CommentaryLine { Moment = moment, Text = text, AudioPath = file });
                        added++;
                    }
                    catch (Exception ex)
                    {
                        // One failed line costs that line only.
                        OnLineFailed?.Invoke(text, ex);
                    }
                }
            }

            library.Save();
            return added;
        }

        /// <summary>
        /// Asks the model for reusable lines for one moment. Existing lines are
        /// shown so it writes something new rather than rephrasing the pool.
        /// </summary>
        private async Task<string[]> WriteLinesAsync(CommentaryMoment moment, int count,
            CommentaryLibrary library, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("No API key configured for commentary generation");

            string existing = string.Join("\n", library.Lines
                .Where(l => l.Moment == moment)
                .Select(l => "- " + l.Text)
                .Take(40));

            StringBuilder prompt = new StringBuilder();
            prompt.AppendLine("You write short spoken lines for an FPV drone-racing commentator.");
            prompt.AppendLine();
            prompt.AppendLine("MOMENT: " + MomentBrief(moment));
            prompt.AppendLine();
            prompt.AppendLine("RULES:");
            prompt.AppendLine("- Each line stands alone and must work at ANY race, so never mention a pilot, a lap time, a position, a channel or a track by name.");
            prompt.AppendLine("- These fill the space BETWEEN the timing calls, which handle all the numbers. Never invent a time or a result.");
            prompt.AppendLine("- One sentence. Spoken English, natural out loud, no more than about fifteen words.");
            prompt.AppendLine("- Vary the shape: some observations, some wry, some building anticipation. Avoid starting two the same way.");
            prompt.AppendLine("- No emoji, no stage directions, no speaker labels.");
            if (!string.IsNullOrWhiteSpace(Style))
            {
                prompt.AppendLine("- Voice: " + Style.Trim());
            }
            if (existing.Length > 0)
            {
                prompt.AppendLine();
                prompt.AppendLine("ALREADY IN THE LIBRARY — write DIFFERENT lines, not rephrasings of these:");
                prompt.AppendLine(existing);
            }
            prompt.AppendLine();
            prompt.AppendLine($"Write exactly {count} lines. Return ONLY a JSON array of strings.");

            var body = new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = prompt.ToString() } } } },
                generationConfig = new { temperature = 1.2, responseMimeType = "application/json" },
            };

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            using StringContent content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await http.PostAsync(url, content, cancel);
            string payload = await resp.Content.ReadAsStringAsync(cancel);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Commentary model HTTP {(int)resp.StatusCode}");

            using JsonDocument doc = JsonDocument.Parse(payload);
            string text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text").GetString();

            return ParseLines(text);
        }

        /// <summary>
        /// Pulls the lines out of the reply. Tolerates a fenced or prose-wrapped
        /// array, since a model occasionally decorates JSON despite being asked
        /// not to.
        /// </summary>
        internal static string[] ParseLines(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return Array.Empty<string>();

            string s = reply.Trim();
            int start = s.IndexOf('[');
            int end = s.LastIndexOf(']');
            if (start >= 0 && end > start) s = s.Substring(start, end - start + 1);

            try
            {
                using JsonDocument doc = JsonDocument.Parse(s);
                return doc.RootElement.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .ToArray();
            }
            catch
            {
                // Fall back to one line per row, stripped of bullets and quotes.
                return reply.Split('\n')
                    .Select(l => l.Trim().TrimStart('-', '*', ' ').Trim('"', ',', ' '))
                    .Where(l => l.Length > 3 && !l.StartsWith("[") && !l.StartsWith("]"))
                    .ToArray();
            }
        }

        private static string MomentBrief(CommentaryMoment moment)
        {
            switch (moment)
            {
                case CommentaryMoment.Staged:
                    return "The field is on the line, about to start. Anticipation, nerves, the moment before the tone.";
                case CommentaryMoment.Launch:
                    return "The race has just started and the pack is away.";
                case CommentaryMoment.MidRace:
                    return "Mid-race, filling the space between timing calls. Observations about the racing, the pace, the pressure.";
                case CommentaryMoment.Finish:
                    return "The race has just finished. Reaction to a heat being done.";
                case CommentaryMoment.BetweenRaces:
                    return "Between heats, while the next field gets ready.";
                default:
                    return "General race-day commentary.";
            }
        }
    }
}
