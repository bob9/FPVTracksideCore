using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sound.AI
{
    /// <summary>
    /// Google Gemini text-to-speech.
    ///
    /// Delivery is steered by natural language glued to the front of the text
    /// ("Say this like a race commentator: …"), which the model follows but
    /// must not read aloud — hence the instruction to speak only the script.
    /// Audio comes back as base64 PCM with the sample rate in a MIME string.
    /// </summary>
    public class GeminiTtsProvider : ITextToSpeechProvider
    {
        public const string DefaultModel = "gemini-2.5-flash-preview-tts";

        private readonly string apiKey;
        private readonly string model;
        private readonly HttpClient http;

        public string Name => "Google Gemini";

        public GeminiTtsProvider(string apiKey, string model = null, HttpClient client = null)
        {
            this.apiKey = (apiKey ?? "").Trim();
            this.model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
            http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        /// <summary>
        /// Gemini's prebuilt voices. Fixed rather than fetched: the API has no
        /// voice-list endpoint, and a stale hard-coded list is easier to
        /// diagnose than an empty picker.
        /// </summary>
        public IEnumerable<TtsVoice> GetVoices()
        {
            (string id, string note)[] voices =
            {
                ("Puck", "upbeat male"),
                ("Fenrir", "excitable male"),
                ("Orus", "firm, deeper male"),
                ("Charon", "informative male"),
                ("Iapetus", "clear male"),
                ("Alnilam", "firm male"),
                ("Rasalgethi", "broadcast male"),
                ("Achird", "friendly male"),
                ("Algenib", "gravelly male"),
                ("Zubenelgenubi", "casual male"),
                ("Kore", "firm female"),
                ("Leda", "youthful female"),
                ("Zephyr", "bright female"),
                ("Aoede", "breezy female"),
                ("Despina", "smooth female"),
                ("Erinome", "clear female"),
                ("Laomedeia", "upbeat female"),
                ("Gacrux", "mature female"),
                ("Autonoe", "bright female"),
                ("Sulafat", "warm female"),
            };

            foreach ((string id, string note) in voices)
            {
                yield return new TtsVoice { Id = id, Name = id, Note = note };
            }
        }

        public async Task SynthesiseToFileAsync(string text, string voiceId, string outputPath, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new TtsException("No Gemini API key configured", text);

            string prompt = string.IsNullOrWhiteSpace(Style)
                ? text
                : Style.TrimEnd() + " Read only the words that follow, exactly as written:\n\n" + text;

            var body = new
            {
                contents = new[]
                {
                    new { role = "user", parts = new[] { new { text = prompt } } }
                },
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" },
                    speechConfig = new
                    {
                        voiceConfig = new
                        {
                            prebuiltVoiceConfig = new { voiceName = string.IsNullOrWhiteSpace(voiceId) ? "Puck" : voiceId }
                        }
                    }
                }
            };

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            using StringContent content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await http.PostAsync(url, content, cancel);
            string payload = await resp.Content.ReadAsStringAsync(cancel);

            if (!resp.IsSuccessStatusCode)
                throw new TtsException($"Gemini HTTP {(int)resp.StatusCode}: {Truncate(payload)}", text);

            using JsonDocument doc = JsonDocument.Parse(payload);
            if (!TryReadInlineAudio(doc, out string b64, out int rate))
                throw new TtsException("Gemini returned no audio (it may have answered in text instead of speaking)", text);

            byte[] pcm = Convert.FromBase64String(b64);
            byte[] samples = WavWriter.ExtractPcm(pcm, out int embedded);
            int outRate = rate > 0 ? rate : embedded;
            WavWriter.WritePcm(outputPath, WavWriter.TrimSilence(samples, outRate), outRate);
        }

        /// <summary>
        /// Optional delivery direction, e.g. "Say this as an energetic race
        /// commentator." Applied to every line so a whole voice pack is
        /// consistent.
        /// </summary>
        public string Style { get; set; }

        private static bool TryReadInlineAudio(JsonDocument doc, out string base64, out int sampleRate)
        {
            base64 = null;
            sampleRate = 0;
            if (!doc.RootElement.TryGetProperty("candidates", out JsonElement cands)) return false;

            foreach (JsonElement cand in cands.EnumerateArray())
            {
                if (!cand.TryGetProperty("content", out JsonElement contentEl)) continue;
                if (!contentEl.TryGetProperty("parts", out JsonElement parts)) continue;

                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("inlineData", out JsonElement inline)) continue;
                    if (!inline.TryGetProperty("data", out JsonElement dataEl)) continue;

                    base64 = dataEl.GetString();
                    if (inline.TryGetProperty("mimeType", out JsonElement mimeEl))
                    {
                        sampleRate = ParseRate(mimeEl.GetString());
                    }
                    return !string.IsNullOrEmpty(base64);
                }
            }
            return false;
        }

        /// <summary>Pulls the rate out of a MIME like "audio/L16;rate=24000".</summary>
        public static int ParseRate(string mime)
        {
            if (string.IsNullOrEmpty(mime)) return 0;
            int i = mime.IndexOf("rate=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;

            int start = i + 5, end = start;
            while (end < mime.Length && char.IsDigit(mime[end])) end++;
            return int.TryParse(mime.Substring(start, end - start), out int rate) ? rate : 0;
        }

        public async Task<bool> TestAsync(CancellationToken cancel)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "fpvts-gemini-test.wav");
            try
            {
                await SynthesiseToFileAsync("Testing.", "Puck", tmp, cancel);
                return new FileInfo(tmp).Length > 1000;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 300 ? s : s.Substring(0, 300);
        }
    }
}
