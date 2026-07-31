#if !MAC

using Composition;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tools;

namespace Sound
{
    public class SpeechManager : IDisposable
    {

        private WorkQueue ttsQueue;

        private ISpeaker speaker;

        private bool mute;
        public bool Muted
        {
            get
            {
                return mute;
            }
            set
            {
                mute = value;
                if (mute)
                {
                    StopSpeech();
                }
            }
        }

        private PlatformTools platformTools;

        public string Voice { get; private set; }

        public int Volume { get; set; }

        /// <summary>
        /// Pre-generated voice pack, when one is configured and built. Race
        /// calls then play from disk instead of being synthesised, which is the
        /// only way a lap time can be spoken the instant it is set.
        /// </summary>
        public AI.VoicePackSpeaker VoicePack { get; private set; }

        /// <summary>Colour between the calls. Always yields to a real call.</summary>
        public AI.CommentaryPlayer Commentary { get; set; }

        public SpeechManager(PlatformTools platformTools, string voice, int volume)
        {
            Voice = voice;
            // Volume zero IS the mute, applied here where it cannot be lost.
            //
            // It used to rely on two things that both fail on the Mac build:
            // EventLayer assigns MuteTTS through a setter that silently returns
            // while speechManager is still null (an init-order swallow), and the
            // per-call speaker.SetVolume(0) lands on MacSpeaker's empty stub.
            // Net effect: TextToSpeechVolume=0 never silenced macOS — the race
            // director muted the tool that manages this file, restarted, and
            // heard two voices calling every lap.
            mute = volume <= 0;
            System.IO.File.AppendAllText("/tmp/macspeaker.log",
                DateTime.Now.ToString("HH:mm:ss.fff") +
                $" SpeechManager ctor: voice={voice} volume={volume} mute={mute}\n");
            this.platformTools = platformTools;
            speaker = platformTools.CreateSpeaker(voice);

            if (speaker != null)
            {
                ttsQueue = new WorkQueue("Speecher TTS");
                // Respects the volume-derived mute above. This line used to be
                // an unconditional Muted = false, which quietly undid any
                // startup mute five lines after it was decided — the reason
                // TextToSpeechVolume=0 never silenced anything.
                Muted = mute;
            }
            else
            {
                mute = true;
            }

            Volume = volume;
        }

        public void Dispose()
        {
            DisposeSpeech();
            ttsQueue?.Dispose();
            ttsQueue = null;
        }

        private void DisposeSpeech()
        {
            StopSpeech();

            if (speaker != null)
            {
                lock (speaker)
                {
                    if (speaker != null)
                    {
                        speaker.Dispose();
                        speaker = null;
                    }
                }
            }
        }
        public bool HasSpeech()
        {
            return ttsQueue != null;
        }

        public bool IsSpeaking
        {
            get { return ttsQueue?.NeedWorkDone ?? false; }
        }

        public void StopSpeech()
        {
            ttsQueue?.Clear();
            if (speaker != null)
            {
                speaker.Stop();
            }
        }

        public void EnqueueSpeech(SpeechRequest speech)
        {
            // A timing call outranks colour absolutely: cut the filler now
            // rather than letting it talk over a lap time.
            Commentary?.Interrupt();

            if (ttsQueue == null)
            {
                speech.OnFinish?.Invoke();
                return;
            }

            // Muted requests flow through Speak like any other, on purpose.
            //
            // The old short-circuit fired OnFinish immediately and skipped Speak
            // entirely, which broke two things nobody connected to "mute": the
            // race-start countdown COLLAPSED (the start tone is chained off this
            // callback, so "in less than five" became zero seconds), and the
            // SOUNDLOG TTS line was never written — which external tools tail to
            // know what the app is saying. A muted broadcaster still follows the
            // running order; it just has the fader down. Speak() logs, then
            // holds the request for the estimated spoken duration, and only the
            // audio itself is suppressed.
            SoundWorkItem soundWorkItem = new SoundWorkItem()
            {
                Action = () => { Speak(speech); },
                Priority = speech.Priority
            };
            ttsQueue.Enqueue(soundWorkItem);
        }

        /// <summary>
        /// Routes race calls through a pre-generated pack. The pack wraps the
        /// platform speaker as its fallback, so a phrase it cannot assemble is
        /// still spoken live rather than dropped.
        /// </summary>
        /// <summary>
        /// Speaks calls generated on demand instead of from pre-made clips.
        ///
        /// Layered ON TOP of whatever is already in place — usually the voice
        /// pack — so anything the generator cannot say still gets spoken. That
        /// ordering is deliberate: live generation is the better experience but
        /// the newer, less proven path, and a race must never go quiet because
        /// a model failed to load.
        /// </summary>
        public bool UseLiveSpeech(AI.LiveSpeechBackend backend)
        {
            if (backend == null || !backend.Ready) return false;

            if (speaker == null)
            {
                speaker = platformTools.CreateSpeaker(Voice);
            }

            AI.LiveSpeechSpeaker live = new AI.LiveSpeechSpeaker(backend, speaker);
            LiveSpeech = live;
            speaker = live;
            return true;
        }

        /// <summary>The live generator, when one is running.</summary>
        public AI.LiveSpeechSpeaker LiveSpeech { get; private set; }

        public void UseVoicePack(AI.VoicePack pack)
        {
            if (pack == null)
            {
                VoicePack = null;
                return;
            }
            if (speaker == null)
            {
                speaker = platformTools.CreateSpeaker(Voice);
            }
            AI.VoicePackSpeaker packSpeaker = new AI.VoicePackSpeaker(pack, speaker);
            packSpeaker.Preload(); // so the first call of the meeting is as quick as the rest
            VoicePack = packSpeaker;
            speaker = packSpeaker;
        }

        private void Speak(SpeechRequest request)
        {
            if (speaker == null)
            {
                speaker = platformTools.CreateSpeaker(Voice);
            }

            if (speaker == null)
                return;

            speaker.SetRate(Math.Max(-10, Math.Min(10, request.Rate)));

            float factorVolume = (request.Volume / 100.0f) * (Volume / 100.0f);

            speaker.SetVolume(Math.Clamp((int)(factorVolume * 100), 0, 100));

            SoundWorkItem[] soundWorkItems = ttsQueue.WorkItems.OfType<SoundWorkItem>().ToArray();

            int maxPriority = 0;
            if (soundWorkItems.Any())
            {
                maxPriority = soundWorkItems.Select(r => r.Priority).Max();
            }

            if (request.Expiry > DateTime.Now && maxPriority <= request.Priority)
            {
                try
                {
                    string text = SpeechParameters.CreateTextToSpeech(request.RawText, request.Parameters);
                    Logger.SoundLog.Log(this, "TTS", text, Logger.LogType.Notice);
                    if (!Muted)
                    {
                        if (!string.IsNullOrEmpty(text))
                        {
                            speaker.Speak(text);
                        }
                    }
                    else if (!string.IsNullOrEmpty(text))
                    {
                        // The time the words would have taken, without the words —
                        // so everything chained off OnFinish (the start tone above
                        // all) keeps its real-world pacing while muted. ~14 chars
                        // per second matches the system voice within a beat.
                        double secs = Math.Clamp(text.Length / 14.0, 0.5, 10.0);
                        System.Threading.Thread.Sleep(TimeSpan.FromSeconds(secs));
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Logger.SoundLog.LogException(this, ex);
                }
                request.OnFinish?.Invoke();
            }
        }
    }

    public class SpeechRequest : SoundRequest
    {
        public int Rate { get; set; }

        public string RawText { get; set; }

        public SpeechParameters Parameters { get; private set; }

        public SpeechRequest(string rawText, int rate, int volume, SpeechParameters parameters, DateTime expiry,Action onFinish)
             : base(parameters.Priority, volume, expiry, onFinish)
        {
            Rate = rate;
            RawText = rawText;
            Parameters = parameters;
        }
    }
}
#endif