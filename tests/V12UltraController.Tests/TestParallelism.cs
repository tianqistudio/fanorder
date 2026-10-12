using Xunit;

// Keep the fake-device suite bounded on a workstation running other workloads.
// Individual tests still exercise concurrent operations explicitly; unrelated
// fixture collections do not need one worker per logical processor.
[assembly: CollectionBehavior(MaxParallelThreads = 2)]
