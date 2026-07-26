using System;
using System.Collections.Generic;
using System.IO;
using System.ComponentModel;
using System.Xml.Serialization;
using Tools;

namespace Sound.AI
{
    /// <summary>
    /// Everything about AI voices, configured here in FPVTrackside rather than
    /// on a server — the venue laptop already knows the timing, and a race day
    /// should not depend on an internet connection to speak.
    ///
    /// The provider keys are only used when GENERATING a voice pack. Once the
    /// pack exists, racing is entirely offline: the calls are files on disk.
    /// </summary>
    public class AISpeechSettings
    {
        private const string filename = "AISpeech.xml";

        [Category("Voice")]
        /// <summary>Off = the platform's built-in speech, exactly as before.</summary>
        public bool Enabled { get; set; }

        [Category("Voice")]
        public TtsProviderKind Provider { get; set; }

        [Category("Voice")]
        /// <summary>Voice id: a provider voice, or a cloned voice's id.</summary>
        public string VoiceId { get; set; }

        [Category("Voice")]
        public string GeminiApiKey { get; set; }

        [Category("Voice")]
        public string QwenApiKey { get; set; }

        [Category("Voice")]
        /// <summary>
        /// Delivery direction baked into every generated clip — an accent, an
        /// energy level, a character. Changing it means regenerating the pack,
        /// since it affects how each fragment was spoken.
        /// </summary>
        public string VoiceStyle { get; set; }

        [Category("Commentary")]
        /// <summary>
        /// Colour between the timing calls. Off by default: the calls are the
        /// job, and commentary is the extra.
        /// </summary>
        public bool CommentaryEnabled { get; set; }

        [Category("Commentary")]
        /// <summary>
        /// Roughly how often a filler line may play, in seconds. It is only ever
        /// a maximum — a line is dropped rather than delaying a race call.
        /// </summary>
        public int CommentaryEverySeconds { get; set; }

        [Category("Commentary")]
        /// <summary>New lines to write per moment when growing the library.</summary>
        public int CommentaryLinesPerBatch { get; set; }

        [Category("Commentary")]
        /// <summary>Flavour for written lines, e.g. "dry, understated humour".</summary>
        public string CommentaryStyle { get; set; }

        [Category("Commentary")]
        /// <summary>Model that writes the lines. Fast and non-thinking by default.</summary>
        public string CommentaryModel { get; set; }

        public AISpeechSettings()
        {
            Enabled = false;
            Provider = TtsProviderKind.Gemini;
            VoiceId = "Puck";
            VoiceStyle = "Speak as an energetic FPV drone-racing commentator: clear, punchy, and easy to understand over a PA.";
            CommentaryEnabled = false;
            CommentaryEverySeconds = 25;
            CommentaryLinesPerBatch = 8;
            CommentaryStyle = "";
            CommentaryModel = CommentaryGenerator.DefaultModel;
        }

        /// <summary>Where a voice's fragments live. One folder per voice, so several can coexist.</summary>
        public string PackDirectory(string profileDirectory)
        {
            string voice = string.IsNullOrWhiteSpace(VoiceId) ? "default" : VoiceId;
            foreach (char c in Path.GetInvalidFileNameChars()) voice = voice.Replace(c, '_');
            return Path.Combine(profileDirectory, "AIVoice", voice);
        }

        /// <summary>Commentary sits beside the fragments — same voice, same folder.</summary>
        public string CommentaryDirectory(string profileDirectory)
        {
            return Path.Combine(PackDirectory(profileDirectory), "commentary");
        }

        /// <summary>Builds the configured provider, or null when its key is missing.</summary>
        public ITextToSpeechProvider CreateProvider()
        {
            switch (Provider)
            {
                case TtsProviderKind.Qwen:
                    if (string.IsNullOrWhiteSpace(QwenApiKey)) return null;
                    return new QwenTtsProvider(QwenApiKey) { Style = VoiceStyle };
                case TtsProviderKind.Gemini:
                default:
                    if (string.IsNullOrWhiteSpace(GeminiApiKey)) return null;
                    return new GeminiTtsProvider(GeminiApiKey) { Style = VoiceStyle };
            }
        }

        /// <summary>
        /// The key used for WRITING commentary. Always Gemini: Qwen is wired
        /// here for its voices (particularly cloned ones), not for text.
        /// </summary>
        public string CommentaryApiKey => GeminiApiKey;

        public static AISpeechSettings Read(Profile profile)
        {
            try
            {
                AISpeechSettings[] s = IOTools.Read<AISpeechSettings>(profile, filename);
                if (s != null && s.Length > 0)
                {
                    Write(profile, s[0]); // round-trip so new fields appear in the file
                    return s[0];
                }
                AISpeechSettings created = new AISpeechSettings();
                Write(profile, created);
                return created;
            }
            catch
            {
                return new AISpeechSettings();
            }
        }

        public static void Write(Profile profile, AISpeechSettings settings)
        {
            try
            {
                IOTools.Write(profile, filename, settings);
            }
            catch
            {
                // Settings failing to save must never stop a race starting.
            }
        }

        public override string ToString()
        {
            return "AI Speech";
        }
    }

    public enum TtsProviderKind
    {
        Gemini,
        Qwen,
    }
}
