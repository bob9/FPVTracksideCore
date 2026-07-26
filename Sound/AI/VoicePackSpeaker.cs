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
        /// Milliseconds each fragment overlaps the next. Long enough to hide
        /// the seam between two separately-synthesised clips, short enough not
        /// to slur the words together.
        /// </summary>
        public int OverlapMs { get; set; } = 18;

        /// <summary>Assembled calls kept in memory before the cache is cleared.</summary>
        public int MaxCachedCalls { get; set; } = 400;

        private readonly Dictionary<string, SoundEffect> callCache =
            new Dictionary<string, SoundEffect>(StringComparer.Ordinal);

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

            SoundEffect call = BuildCall(text, files);
            if (call == null) { fallback?.Speak(text); return; }

            lock (playLock)
            {
                if (stopped) return;
                PlayWhole(call);
                FragmentsPlayed += files.Length;
            }
        }

        /// <summary>
        /// Assembles a whole call into ONE sound, crossfading the joins.
        ///
        /// Playing the fragments as separate sounds was what made calls jerky:
        /// each boundary carried the scheduling gap of starting another sound,
        /// and the hard cut between two independently-synthesised clips clicks
        /// because their waveforms do not meet at zero. As a single buffer the
        /// call is continuous, and it is cached so a repeated call costs
        /// nothing at all.
        /// </summary>
        private SoundEffect BuildCall(string text, string[] files)
        {
            string key = string.Join("|", files);
            lock (callCache)
            {
                if (callCache.TryGetValue(key, out SoundEffect cached)) return cached;
            }

            try
            {
                List<byte[]> parts = new List<byte[]>(files.Length);
                int rate = WavWriter.DefaultSampleRate;

                foreach (string f in files)
                {
                    byte[] pcm = WavWriter.ExtractPcm(File.ReadAllBytes(f), out int r);
                    if (pcm.Length == 0) continue;
                    rate = r;
                    parts.Add(pcm);
                }
                if (parts.Count == 0) return null;

                byte[] blended = WavWriter.Blend(parts, rate, OverlapMs);
                SoundEffect effect = new SoundEffect(blended, rate, AudioChannels.Mono);

                lock (callCache)
                {
                    // Bounded: a meeting produces many distinct calls and each
                    // holds its audio in memory.
                    if (callCache.Count > MaxCachedCalls) callCache.Clear();
                    callCache[key] = effect;
                }
                return effect;
            }
            catch (Exception ex)
            {
                Logger.SoundLog?.LogException(this, ex);
                return null;
            }
        }

        private void PlayWhole(SoundEffect call)
        {
            SoundEffectInstance instance = call.CreateInstance();
            instance.Volume = volume;

            lock (playing) playing.Add(instance);
            try
            {
                instance.Play();
                // One sound now, so this simply waits for the call to finish —
                // and still notices a higher-priority call stopping it.
                while (instance.State == SoundState.Playing && !stopped)
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
            lock (callCache)
            {
                foreach (SoundEffect e in callCache.Values)
                {
                    try { e.Dispose(); } catch { }
                }
                callCache.Clear();
            }
            fallback?.Dispose();
        }
    }
}
