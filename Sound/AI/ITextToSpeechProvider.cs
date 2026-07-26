using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sound.AI
{
    /// <summary>
    /// A cloud text-to-speech service that renders a line of text to audio.
    ///
    /// Nothing here is ever called during a race. Cloud synthesis takes one to
    /// several seconds, which is far too slow for a lap call — the pilot has
    /// flown most of the next lap by the time it returns. Providers are used
    /// ONLY to pre-generate a voice pack ahead of the meeting; at race time the
    /// audio is already on disk and playback is instant.
    /// </summary>
    public interface ITextToSpeechProvider
    {
        /// <summary>Name shown in the settings UI.</summary>
        string Name { get; }

        /// <summary>Voices this provider offers, for the voice picker.</summary>
        IEnumerable<TtsVoice> GetVoices();

        /// <summary>
        /// Renders text to a 16-bit mono PCM WAV file at <paramref name="outputPath"/>.
        /// Throws on failure so the generator can report which line failed.
        /// </summary>
        Task SynthesiseToFileAsync(string text, string voiceId, string outputPath, CancellationToken cancel);

        /// <summary>
        /// Cheap check that the key/config works, so a mistyped key is caught in
        /// settings rather than half way through generating a voice pack.
        /// </summary>
        Task<bool> TestAsync(CancellationToken cancel);
    }

    /// <summary>One selectable voice.</summary>
    public class TtsVoice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Note { get; set; }

        /// <summary>
        /// True for a voice cloned on the user's own account rather than one of
        /// the provider's built-ins.
        /// </summary>
        public bool Cloned { get; set; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Note) ? Name : Name + " — " + Note;
        }
    }

    /// <summary>Raised when synthesis fails, carrying the text that failed.</summary>
    public class TtsException : Exception
    {
        public string Text { get; }

        public TtsException(string message, string text, Exception inner = null)
            : base(message, inner)
        {
            Text = text;
        }
    }
}
