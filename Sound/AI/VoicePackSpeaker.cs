using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework.Audio;
using Tools;

namespace Sound.AI
{
    /// <summary>
    /// An ISpeaker that plays pre-generated fragments instead of calling a
    /// cloud service, so a lap call is audible immediately.
    ///
    /// It is an ISpeaker on purpose. SoundManager, the priority queue, the
    /// expiry rules and every existing race call stay exactly as they are —
    /// this only changes how the words become sound. Anything the pack cannot
    /// say falls through to the platform's own speaker, so an unexpected phrase
    /// is still spoken rather than silently dropped.
    /// </summary>
    public class VoicePackSpeaker : ISpeaker
    {
        private readonly VoicePack pack;
        private readonly ISpeaker fallback;
        private readonly object playLock = new object();

        private readonly Dictionary<string, SoundEffect> cache =
            new Dictionary<string, SoundEffect>(StringComparer.OrdinalIgnoreCase);

        private readonly List<SoundEffectInstance> playing = new List<SoundEffectInstance>();
        private float volume = 1.0f;
        private volatile bool stopped;

        /// <summary>
        /// Fraction of each clip to let run before starting the next, 0.5-1.0.
        /// Below 1 the clips overlap slightly, which is how a real commentator
        /// runs words together — a race call has to be quick, and playing each
        /// clip to its very last sample makes it drag.
        /// </summary>
        public float Pace { get; set; } = 0.88f;

        /// <summary>Fragments played from the pack, for diagnostics.</summary>
        public int FragmentsPlayed { get; private set; }

        /// <summary>Calls that had to fall back to live TTS.</summary>
        public int FallbackCalls { get; private set; }

        public VoicePackSpeaker(VoicePack pack, ISpeaker fallback)
        {
            this.pack = pack;
            this.fallback = fallback;
        }

        public IEnumerable<string> GetVoices()
        {
            string name = pack?.VoiceName ?? pack?.VoiceId;
            if (!string.IsNullOrEmpty(name)) yield return name;

            if (fallback == null) yield break;
            foreach (string v in fallback.GetVoices()) yield return v;
        }

        public void SelectVoice(string voice)
        {
            // The pack IS the voice; switching happens by choosing a different
            // pack in settings. Passed on so the fallback stays consistent.
            fallback?.SelectVoice(voice);
        }

        public void SetRate(int rate)
        {
            // Pre-generated audio has a fixed rate. Resampling it here would
            // cost the instant playback this exists for, so the setting only
            // reaches the fallback.
            fallback?.SetRate(rate);
        }

        public void SetVolume(int v)
        {
            volume = Math.Clamp(v, 0, 100) / 100.0f;
            fallback?.SetVolume(v);
        }

        public void Speak(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            stopped = false;

            string[] files = pack?.ResolveFiles(text);
            if (files == null || files.Length == 0)
            {
                // Something in the line was never generated. Speaking it live is
                // slow, but a missing word is worse than a late one.
                FallbackCalls++;
                Logger.SoundLog?.Log(this, "VoicePack", "no fragments for: " + text, Logger.LogType.Notice);
                fallback?.Speak(text);
                return;
            }

            lock (playLock)
            {
                foreach (string file in files)
                {
                    if (stopped) return;
                    PlayBlocking(file);
                    FragmentsPlayed++;
                }
            }
        }

        /// <summary>
        /// Plays one fragment and waits for it, so a call comes out as a
        /// sentence rather than every word at once.
        /// </summary>
        private void PlayBlocking(string file)
        {
            SoundEffect effect = Load(file);
            if (effect == null) return;

            SoundEffectInstance instance = effect.CreateInstance();
            instance.Volume = volume;

            lock (playing) playing.Add(instance);
            try
            {
                instance.Play();

                // Move on slightly before the clip ends so words run together
                // the way speech does. Polling rather than sleeping the whole
                // duration also means a higher-priority call can cut in.
                double budgetMs = effect.Duration.TotalMilliseconds * Math.Clamp(Pace, 0.5f, 1.0f);
                DateTime deadline = DateTime.Now.AddMilliseconds(budgetMs);

                while (instance.State == SoundState.Playing && !stopped && DateTime.Now < deadline)
                {
                    System.Threading.Thread.Sleep(5);
                }
            }
            finally
            {
                lock (playing) playing.Remove(instance);
                try { instance.Dispose(); } catch { }
            }
        }

        private SoundEffect Load(string file)
        {
            lock (cache)
            {
                if (cache.TryGetValue(file, out SoundEffect cached)) return cached;
            }

            try
            {
                using FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                SoundEffect effect = SoundEffect.FromStream(fs);
                lock (cache) cache[file] = effect;
                return effect;
            }
            catch (Exception ex)
            {
                Logger.SoundLog?.LogException(this, ex);
                return null;
            }
        }

        /// <summary>
        /// Loads every fragment into memory. Called once when the pack is
        /// selected so the first lap of the meeting is as quick as the rest —
        /// otherwise the very call that matters most pays the disk read.
        /// </summary>
        public void Preload()
        {
            if (pack == null) return;
            foreach (KeyValuePair<string, string> f in pack.Fragments.ToArray())
            {
                if (File.Exists(f.Value)) Load(f.Value);
            }
        }

        public void Stop()
        {
            stopped = true;
            lock (playing)
            {
                foreach (SoundEffectInstance i in playing.ToArray())
                {
                    try { i.Stop(); } catch { }
                }
            }
            fallback?.Stop();
        }

        public void Dispose()
        {
            Stop();
            lock (cache)
            {
                foreach (SoundEffect e in cache.Values)
                {
                    try { e.Dispose(); } catch { }
                }
                cache.Clear();
            }
            fallback?.Dispose();
        }
    }
}
