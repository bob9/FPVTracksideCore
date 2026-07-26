using System;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using Tools;

namespace Sound.AI
{
    /// <summary>
    /// Plays pre-generated colour between the timing calls.
    ///
    /// The rule that shapes this: a race call ALWAYS wins. Commentary is filler
    /// and is treated as such — it is only offered when the bus is genuinely
    /// idle, it is cut the instant a real call arrives, and if in doubt it says
    /// nothing. Pilots and the race director need the times; the colour is
    /// there to stop the silence sounding dead.
    ///
    /// Because every line is already on disk, offering one costs nothing and
    /// dropping one costs nothing either.
    /// </summary>
    public class CommentaryPlayer : IDisposable
    {
        private readonly CommentaryLibrary library;
        private readonly object playLock = new object();

        private SoundEffectInstance current;
        private DateTime lastPlayed = DateTime.MinValue;

        /// <summary>Minimum spacing between filler lines.</summary>
        public TimeSpan MinimumGap { get; set; } = TimeSpan.FromSeconds(25);

        public float Volume { get; set; } = 1.0f;

        /// <summary>Lines played this session, for the settings page.</summary>
        public int Played { get; private set; }

        /// <summary>Times a line was cut because a race call came in.</summary>
        public int Interrupted { get; private set; }

        public CommentaryPlayer(CommentaryLibrary library)
        {
            this.library = library;
        }

        public bool IsPlaying
        {
            get
            {
                lock (playLock)
                {
                    return current != null && current.State == SoundState.Playing;
                }
            }
        }

        /// <summary>
        /// Offers a line for this moment. Returns false — and says nothing —
        /// when it is too soon, something is already playing, or the library
        /// has nothing suitable. Never blocks the caller.
        /// </summary>
        public bool TryPlay(CommentaryMoment moment, bool busIdle)
        {
            if (library == null || !busIdle) return false;
            if (DateTime.Now - lastPlayed < MinimumGap) return false;
            if (IsPlaying) return false;

            CommentaryLine line = library.Pick(moment);
            if (line == null || !File.Exists(line.AudioPath)) return false;

            try
            {
                using FileStream fs = new FileStream(line.AudioPath, FileMode.Open, FileAccess.Read);
                SoundEffect effect = SoundEffect.FromStream(fs);
                SoundEffectInstance instance = effect.CreateInstance();
                instance.Volume = Math.Clamp(Volume, 0f, 1f);

                lock (playLock)
                {
                    current = instance;
                }
                instance.Play();
                lastPlayed = DateTime.Now;
                Played++;
                return true;
            }
            catch (Exception ex)
            {
                Logger.SoundLog?.LogException(this, ex);
                return false;
            }
        }

        /// <summary>
        /// Stops immediately. Called the moment a race call is queued — a lap
        /// time talked over by filler is worse than no filler at all.
        /// </summary>
        public void Interrupt()
        {
            lock (playLock)
            {
                if (current == null) return;
                if (current.State == SoundState.Playing)
                {
                    try { current.Stop(); Interrupted++; } catch { }
                }
                try { current.Dispose(); } catch { }
                current = null;
            }
        }

        public void Dispose()
        {
            Interrupt();
        }
    }
}
