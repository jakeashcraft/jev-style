namespace JevStyle.Benchmark.Dataset;

/// <summary>mulberry32, the seeded PRNG bench.mts uses to generate data/tags.json. Same seed, same sequence.</summary>
internal sealed class SeededRandom(uint seed)
{
    private uint _state = seed;

    public double NextDouble()
    {
        unchecked
        {
            _state += 0x6D2B79F5;
            uint result = (_state ^ (_state >> 15)) * (_state | 1u);
            result ^= result + ((result ^ (result >> 7)) * (result | 61u));
            return (result ^ (result >> 14)) / 4294967296.0;
        }
    }
}
