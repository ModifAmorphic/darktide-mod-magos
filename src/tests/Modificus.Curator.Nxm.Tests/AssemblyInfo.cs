using Xunit;

// Real named pipes (System.IO.Pipes) are an OS-level shared resource: the
// NxmIpcServer + framing round-trip tests bind + connect + dispose pipes under
// the /tmp/.dotnet-core-pipe/ directory. Running these in parallel with each
// other (or with heavy fake-pipe tests that spawn processes) induces
// timing-sensitive flakes. Disable test parallelization for this assembly: the
// full suite runs in well under a second serialized, and the determinism is
// worth far more than the few hundred milliseconds.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
