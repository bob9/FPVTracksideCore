using System;
using System.IO;

namespace Sound.AI
{
    /// <summary>
    /// Minimal RIFF/WAVE reading and writing for voice-pack audio.
    ///
    /// Both providers hand back 16-bit mono PCM but package it differently —
    /// Gemini returns bare samples with the rate in a MIME type, Qwen returns a
    /// WAV whose declared chunk sizes are streaming placeholders (0x7FFFFF..)
    /// rather than real lengths. Trusting those sizes reads past the buffer, so
    /// a declared length that overruns what is present means "to the end".
    /// </summary>
    public static class WavWriter
    {
        public const int DefaultSampleRate = 24000;

        /// <summary>Wraps raw 16-bit mono PCM in a WAV container.</summary>
        public static void WritePcm(string path, byte[] pcm, int sampleRate = DefaultSampleRate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using BinaryWriter w = new BinaryWriter(fs);

            const short channels = 1;
            const short bitsPerSample = 16;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);

            w.Write(new char[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + pcm.Length);
            w.Write(new char[] { 'W', 'A', 'V', 'E' });
            w.Write(new char[] { 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1); // PCM
            w.Write(channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write(bitsPerSample);
            w.Write(new char[] { 'd', 'a', 't', 'a' });
            w.Write(pcm.Length);
            w.Write(pcm);
        }

        /// <summary>
        /// Returns the sample data from a RIFF/WAVE payload, or the input
        /// unchanged when it is not one (some providers stream bare PCM).
        /// </summary>
        public static byte[] ExtractPcm(byte[] data, out int sampleRate)
        {
            sampleRate = DefaultSampleRate;
            if (data == null || data.Length < 12) return data ?? Array.Empty<byte>();
            if (!(data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')) return data;
            if (!(data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E')) return data;

            int pos = 12;
            while (pos + 8 <= data.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                int size = BitConverter.ToInt32(data, pos + 4);
                int payload = pos + 8;
                int avail = data.Length - payload;

                if (id == "fmt " && avail >= 16)
                {
                    sampleRate = BitConverter.ToInt32(data, payload + 4);
                }
                else if (id == "data")
                {
                    // Streaming placeholder or corrupt length: take the rest.
                    if (size < 0 || size > avail) size = avail;
                    byte[] pcm = new byte[size];
                    Buffer.BlockCopy(data, payload, pcm, 0, size);
                    return pcm;
                }

                if (size < 0 || size > avail) break;
                pos = payload + size;
                if ((size & 1) == 1) pos++; // chunks pad to an even boundary
            }
            return data;
        }

        /// <summary>
        /// Joins several WAVs into one, with optional silence between them.
        /// Used to bake a multi-fragment phrase into a single clip so playback
        /// is one file open rather than several.
        /// </summary>
        public static void Concatenate(string outputPath, string[] inputPaths, int gapMs = 0)
        {
            using MemoryStream all = new MemoryStream();
            int rate = DefaultSampleRate;

            foreach (string p in inputPaths)
            {
                if (!File.Exists(p)) continue;
                byte[] pcm = ExtractPcm(File.ReadAllBytes(p), out rate);
                all.Write(pcm, 0, pcm.Length);

                if (gapMs > 0)
                {
                    int silenceBytes = rate * 2 * gapMs / 1000;
                    silenceBytes -= silenceBytes % 2; // stay 16-bit aligned
                    all.Write(new byte[silenceBytes], 0, silenceBytes);
                }
            }
            WritePcm(outputPath, all.ToArray(), rate);
        }
    }
}
