// The telemetry recorder's ActivityListener is process-wide: two runs in parallel would see each other's
// spans and network activity.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
