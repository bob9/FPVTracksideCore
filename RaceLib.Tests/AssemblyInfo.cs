// The EventManager / database stack uses process-global statics (DatabaseFactory,
// Logger, IOTools.WorkingDirectory), so tests must not run concurrently - each
// EventTestBed re-points those statics at its own temp directory.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
