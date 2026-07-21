using System;
using System.IO;
using System.Linq;
using System.Threading;
using RaceLib;
using Tools;

namespace RaceLib.Tests
{
    /// <summary>
    /// Boots a real EventManager backed by the JSON file database in a throw-away
    /// temp directory, so tests can exercise the genuine paste / race-numbering code
    /// paths end to end (no mocks).
    /// </summary>
    public sealed class EventTestBed : IDisposable
    {
        public EventManager EventManager { get; private set; }
        public Profile Profile { get; }
        public Guid EventId { get; }

        private readonly DirectoryInfo dataDir;
        private WorkQueue workQueue;

        public EventTestBed()
        {
            string root = Path.Combine(Path.GetTempPath(), "fpv-tests-" + Guid.NewGuid().ToString("N"));
            dataDir = new DirectoryInfo(root);
            dataDir.Create();

            // The managers log through static Logger.* instances; initialise them so
            // the very first LogCall doesn't NRE.
            Logger.Init(dataDir);

            // IOTools resolves relative paths against this static root (the app sets
            // it from the platform tools at startup).
            IOTools.WorkingDirectory = dataDir;

            // The factory's "event directory" is where the JSON db stores everything.
            DatabaseFactory.Init(new DB.DatabaseFactory(dataDir, dataDir));

            Profile = new Profile(dataDir, "test");

            // Create the event (events live in the data-dir root; Guid.Empty handle).
            Event ev;
            using (IDatabase db = DatabaseFactory.Open(Guid.Empty))
            {
                ev = new Event();
                ev.Name = "Test Event";
                ev.EventType = EventTypes.Race;
                ev.Channels = Channel.IMD6C;
                db.Insert(ev);
            }
            EventId = ev.ID;

            Load();
        }

        /// <summary>
        /// Tear down the current EventManager and re-open the same event from disk -
        /// i.e. simulate the user closing and re-opening the event.
        /// </summary>
        public void Reload()
        {
            try { workQueue?.Dispose(); } catch { }
            try { EventManager?.Dispose(); } catch { }
            Load();
        }

        private void Load()
        {
            EventManager = new EventManager(Profile);
            workQueue = new WorkQueue("test-load");
            WorkSet workSet = new WorkSet();
            EventManager.LoadEvent(workSet, workQueue, EventId);
            DrainQueue();
            EventManager.LoadRaces(workSet, workQueue);
            DrainQueue();
        }

        /// <summary>Block until the async load WorkQueue has finished every item.</summary>
        private void DrainQueue()
        {
            int spins = 0;
            while (workQueue.NeedWorkDone || workQueue.QueueLength > 0)
            {
                Thread.Sleep(10);
                if (++spins > 1000)
                    throw new TimeoutException("Event load did not complete");
            }
        }

        public Pilot AddPilot(string name)
        {
            Pilot p = Pilot.CreateFromName(name);
            EventManager.AddPilot(p, EventManager.Channels.First());
            return p;
        }

        public Round CreateRound()
        {
            return EventManager.RoundManager.GetCreateRound(
                EventManager.RaceManager.GetMaxRoundNumber(EventTypes.Race) + 1,
                EventTypes.Race);
        }

        /// <summary>The valid races for a round, in display order.</summary>
        public Race[] RacesIn(Round round)
        {
            return EventManager.RaceManager.GetRaces(round)
                .OrderBy(r => r.RaceNumber)
                .ToArray();
        }

        public void Dispose()
        {
            try { workQueue.Dispose(); } catch { }
            try { EventManager.Dispose(); } catch { }
            try { if (dataDir.Exists) dataDir.Delete(true); } catch { }
        }
    }
}
