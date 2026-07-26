using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sound.AI
{
    /// <summary>
    /// A growing library of pre-generated commentary lines — the colour between
    /// the race calls.
    ///
    /// These are NOT synthesised during a race. They are written and voiced
    /// ahead of time and played from disk, exactly like the lap fragments, so
    /// commentary can never delay a call. The library grows every time it is
    /// generated, so a club that has run a few meetings has a deep pool and
    /// hears something different each heat.
    ///
    /// Lines are deliberately generic — they talk about racing, not about a
    /// specific lap — because a line about a specific time could only be used
    /// once. Anything needing live numbers is a race call, and race calls come
    /// from the fragment pack.
    /// </summary>
    public class CommentaryLibrary
    {
        public const string IndexFileName = "commentary.json";

        public string Directory { get; }

        private readonly List<CommentaryLine> lines = new List<CommentaryLine>();
        private readonly Random random = new Random();

        /// <summary>Recently played, so the same line is not repeated.</summary>
        private readonly Queue<string> recent = new Queue<string>();

        /// <summary>
        /// How many lines to remember. Set from the library size at load so a
        /// small pool still rotates rather than blocking itself entirely.
        /// </summary>
        public int RecentMemory { get; set; } = 12;

        public CommentaryLibrary(string directory)
        {
            Directory = directory;
        }

        public int Count => lines.Count;

        public IEnumerable<CommentaryLine> Lines => lines;

        public void Add(CommentaryLine line)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.Text)) return;
            lines.Add(line);
        }

        /// <summary>
        /// Picks a line for a moment, avoiding anything played recently.
        /// Returns null when the library has nothing for that moment, which the
        /// caller treats as "say nothing" — silence is better than a line that
        /// does not fit.
        /// </summary>
        public CommentaryLine Pick(CommentaryMoment moment)
        {
            List<CommentaryLine> candidates = lines
                .Where(l => l.Moment == moment && File.Exists(l.AudioPath))
                .ToList();
            if (candidates.Count == 0) return null;

            List<CommentaryLine> fresh = candidates.Where(l => !recent.Contains(l.Id)).ToList();
            // Everything has been heard lately: rather than stay silent, allow a
            // repeat but clear the memory so the rotation restarts.
            if (fresh.Count == 0)
            {
                recent.Clear();
                fresh = candidates;
            }

            CommentaryLine chosen = fresh[random.Next(fresh.Count)];
            recent.Enqueue(chosen.Id);
            while (recent.Count > Math.Max(1, Math.Min(RecentMemory, candidates.Count - 1)))
            {
                recent.Dequeue();
            }
            return chosen;
        }

        public void Save()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var index = lines.Select(l => new
            {
                id = l.Id,
                moment = l.Moment.ToString(),
                text = l.Text,
                file = Path.GetFileName(l.AudioPath),
            });
            File.WriteAllText(Path.Combine(Directory, IndexFileName),
                JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static CommentaryLibrary Load(string directory)
        {
            CommentaryLibrary lib = new CommentaryLibrary(directory);
            string path = Path.Combine(directory, IndexFileName);
            if (!File.Exists(path)) return lib;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (JsonElement e in doc.RootElement.EnumerateArray())
                {
                    string moment = e.TryGetProperty("moment", out var m) ? m.GetString() : null;
                    if (!Enum.TryParse(moment, out CommentaryMoment parsed)) continue;

                    lib.Add(new CommentaryLine
                    {
                        Id = e.TryGetProperty("id", out var i) ? i.GetString() : Guid.NewGuid().ToString("N"),
                        Moment = parsed,
                        Text = e.TryGetProperty("text", out var t) ? t.GetString() : "",
                        AudioPath = Path.Combine(directory, e.TryGetProperty("file", out var f) ? f.GetString() : ""),
                    });
                }
            }
            catch
            {
                // A corrupt index costs the colour, not the race calls.
            }
            return lib;
        }

        /// <summary>True when a line already exists, so re-generating grows the pool rather than duplicating it.</summary>
        public bool HasText(string text)
        {
            string norm = VoicePack.Normalise(text);
            return lines.Any(l => VoicePack.Normalise(l.Text) == norm);
        }
    }

    /// <summary>Where in a race a line fits.</summary>
    public enum CommentaryMoment
    {
        /// <summary>Before the start, field on the line.</summary>
        Staged,
        /// <summary>Just after the start.</summary>
        Launch,
        /// <summary>Between laps, filling space.</summary>
        MidRace,
        /// <summary>After the finish.</summary>
        Finish,
        /// <summary>Between heats.</summary>
        BetweenRaces,
    }

    public class CommentaryLine
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public CommentaryMoment Moment { get; set; }
        public string Text { get; set; }
        public string AudioPath { get; set; }
    }
}
