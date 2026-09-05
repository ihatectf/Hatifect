using Xunit;

// Stardew's SaveSerializer owns an unsynchronized static serializer cache.
// Match its game-thread usage; game-backed tests cannot initialize that cache in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
