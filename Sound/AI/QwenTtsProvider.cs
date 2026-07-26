using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sound.AI
{
    /// <summary>
    /// Alibaba Model Studio (Qwen3-TTS), including voices CLONED on the user's
    /// own account — which is what lets a club use its own commentator.
    ///
    /// Two things differ from Gemini and both matter:
    ///
    ///  - Delivery goes in a separate `instructions` field. Qwen's `text` is
    ///    literally what gets spoken, so a style preamble would be read aloud.
    ///  - Cloned and prebuilt voices are served by DIFFERENT models which
    ///    reject each other's voices outright, so the model is chosen per voice
    ///    rather than configured. A cloned id carries a fixed prefix.
    /// </summary>
    public class QwenTtsProvider : ITextToSpeechProvider
    {
        public const string DefaultBaseUrl = "https://dashscope-intl.aliyuncs.com";
        public const string PrebuiltModel = "qwen3-tts-flash";
        public const string InstructModel = "qwen3-tts-instruct-flash";
        public const string ClonedModel = "qwen3-tts-vc-2026-01-22";
        public const string ClonedVoicePrefix = "qwen-tts-vc-";

        private readonly string apiKey;
        private readonly string baseUrl;
        private readonly HttpClient http;

        public string Name => "Qwen (Alibaba Model Studio)";

        /// <summary>Delivery direction applied to every line.</summary>
        public string Style { get; set; }

        public QwenTtsProvider(string apiKey, string baseUrl = null, HttpClient client = null)
        {
            this.apiKey = (apiKey ?? "").Trim();
            this.baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
            http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        }

        public static bool IsClonedVoice(string voiceId)
        {
            return !string.IsNullOrEmpty(voiceId) &&
                   voiceId.Trim().StartsWith(ClonedVoicePrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Picks the model a voice requires. Not a preference — the cloning and
        /// flash models reject each other's voices, so the wrong one is a hard
        /// API error.
        /// </summary>
        public static string ModelForVoice(string voiceId, bool hasInstruction)
        {
            if (IsClonedVoice(voiceId)) return ClonedModel;
            return hasInstruction ? InstructModel : PrebuiltModel;
        }

        /// <summary>
        /// Built-in voices, curated to those that suit English race commentary.
        /// The full catalogue is around fifty, but most are Chinese-dialect
        /// voices that would only be noise in this picker.
        /// </summary>
        public IEnumerable<TtsVoice> GetVoices()
        {
            (string id, string note)[] voices =
            {
                ("Radio Gol", "sports commentator (male)"),
                ("Ryan", "dramatic flair (male)"),
                ("Neil", "news anchor, precise (male)"),
                ("Vincent", "raspy, characterful (male)"),
                ("Andre", "magnetic, steady (male)"),
                ("Aiden", "American English (male)"),
                ("Ethan", "warm, energetic (male)"),
                ("Eldric Sage", "calm, older (male)"),
                ("Jennifer", "cinematic American English (female)"),
                ("Katerina", "mature, rich (female)"),
                ("Bellona", "powerful, heroic (female)"),
                ("Cherry", "bright, friendly (female)"),
                ("Serena", "measured (female)"),
            };

            foreach ((string id, string note) in voices)
            {
                yield return new TtsVoice { Id = id, Name = id, Note = note };
            }
        }

        /// <summary>
        /// Voices cloned on this account. Listed at runtime because they are
        /// created by the user, not shipped with the app.
        ///
        /// The list response names the id field "voice" (there is no
        /// "voice_id"), gives timestamps as strings, and carries no display
        /// name — the enrolment name is baked into the id.
        /// </summary>
        public async Task<IEnumerable<TtsVoice>> GetClonedVoicesAsync(CancellationToken cancel)
        {
            List<TtsVoice> found = new List<TtsVoice>();
            if (string.IsNullOrWhiteSpace(apiKey)) return found;

            var body = new { model = "qwen-voice-enrollment", input = new { action = "list", page_size = 50 } };
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post,
                baseUrl + "/api/v1/services/audio/tts/customization");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using HttpResponseMessage resp = await http.SendAsync(req, cancel);
            if (!resp.IsSuccessStatusCode) return found;

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancel));
            if (!doc.RootElement.TryGetProperty("output", out JsonElement output)) return found;
            if (!output.TryGetProperty("voice_list", out JsonElement list)) return found;

            foreach (JsonElement v in list.EnumerateArray())
            {
                if (!v.TryGetProperty("voice", out JsonElement idEl)) continue;
                string id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;

                found.Add(new TtsVoice { Id = id, Name = ClonedVoiceName(id), Note = "your cloned voice", Cloned = true });
            }
            return found;
        }

        /// <summary>Recovers the enrolment name from a cloned voice id.</summary>
        public static string ClonedVoiceName(string id)
        {
            string s = (id ?? "").Trim();
            if (s.StartsWith(ClonedVoicePrefix, StringComparison.Ordinal))
                s = s.Substring(ClonedVoicePrefix.Length);

            int i = s.LastIndexOf("-voice-", StringComparison.Ordinal);
            return i > 0 ? s.Substring(0, i) : id;
        }

        public async Task SynthesiseToFileAsync(string text, string voiceId, string outputPath, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new TtsException("No Qwen API key configured", text);

            string instruction = (Style ?? "").Trim();
            Dictionary<string, object> input = new Dictionary<string, object>
            {
                ["text"] = text,
                ["voice"] = voiceId,
            };
            if (instruction.Length > 0) input["instructions"] = instruction;

            // Only a prebuilt voice takes the language hint; a cloned voice
            // takes its language from the enrolment sample.
            if (!IsClonedVoice(voiceId)) input["language_type"] = "English";

            var body = new { model = ModelForVoice(voiceId, instruction.Length > 0), input };

            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post,
                baseUrl + "/api/v1/services/aigc/multimodal-generation/generation");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using HttpResponseMessage resp = await http.SendAsync(req, cancel);
            string payload = await resp.Content.ReadAsStringAsync(cancel);

            if (!resp.IsSuccessStatusCode)
                throw new TtsException($"Qwen HTTP {(int)resp.StatusCode}: {Truncate(payload)}", text);

            using JsonDocument doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("output", out JsonElement output) ||
                !output.TryGetProperty("audio", out JsonElement audio))
            {
                throw new TtsException("Qwen returned no audio", text);
            }

            byte[] raw;
            if (audio.TryGetProperty("data", out JsonElement dataEl) && !string.IsNullOrEmpty(dataEl.GetString()))
            {
                raw = Convert.FromBase64String(dataEl.GetString());
            }
            else if (audio.TryGetProperty("url", out JsonElement urlEl) && !string.IsNullOrEmpty(urlEl.GetString()))
            {
                // The non-streaming call answers with a signed URL that expires,
                // so it is fetched immediately rather than stored.
                raw = await http.GetByteArrayAsync(urlEl.GetString(), cancel);
            }
            else
            {
                throw new TtsException("Qwen response carried neither audio data nor a URL", text);
            }

            byte[] pcm = WavWriter.ExtractPcm(raw, out int rate);
            WavWriter.WritePcm(outputPath, WavWriter.TrimSilence(pcm, rate), rate);
        }

        public async Task<bool> TestAsync(CancellationToken cancel)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "fpvts-qwen-test.wav");
            try
            {
                await SynthesiseToFileAsync("Testing.", "Ethan", tmp, cancel);
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
