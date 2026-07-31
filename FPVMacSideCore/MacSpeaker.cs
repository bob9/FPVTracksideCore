using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;

namespace FPVMacsideCore
{
    public class MacSpeaker : ISpeaker
    {
        private string voice;

        private Process speechProcess;

        public MacSpeaker()
        {
            voice = "Default";
        }

        public void Dispose()
        {
            Stop();
        }

        public IEnumerable<string> GetVoices()
        {
            return new string[] { voice };
        }

        public void SelectVoice(string voice)
        {
        }

        public void SetRate(int rate)
        {
        }

        public void SetVolume(int volume)
        {
        }

        public void Speak(string text)
        {
            // Instrumented after a day of silence nobody could attribute: the
            // SpeechManager logged every line while nothing was heard, and every
            // wrapper (mute, voice pack, live speech) was ruled out one by one.
            // This journal answers, per call, whether say was launched, with
            // what, and how it exited — the three facts that were unknowable.
            try
            {
                System.IO.File.AppendAllText("/tmp/macspeaker.log",
                    DateTime.Now.ToString("HH:mm:ss.fff") + " speak: " + text + "\n");
                string cmdArgs = text;
                speechProcess = Process.Start("/usr/bin/say", cmdArgs);
                System.IO.File.AppendAllText("/tmp/macspeaker.log",
                    DateTime.Now.ToString("HH:mm:ss.fff") + " started pid " + speechProcess.Id + "\n");
                speechProcess.WaitForExit();
                System.IO.File.AppendAllText("/tmp/macspeaker.log",
                    DateTime.Now.ToString("HH:mm:ss.fff") + " exited " + speechProcess.ExitCode + "\n");
                speechProcess = null;
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText("/tmp/macspeaker.log",
                    DateTime.Now.ToString("HH:mm:ss.fff") + " FAILED: " + ex + "\n");
                throw;
            }
        }

        public void Stop()
        {
            Process process = speechProcess;
            if (process != null)
            {
                process.Kill();
                process = null;
            }
        }
    }
}
