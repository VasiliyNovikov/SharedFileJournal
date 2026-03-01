using System;
using System.IO.Hashing;
using System.Security.Cryptography;

using BenchmarkDotNet.Attributes;

namespace SharedFileJournal.Benchmarks;

[MemoryDiagnoser]
public class ChecksumBenchmarks
{
    private byte[] _data = null!;

    [Params(16, 256, 4096, 65536)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = RandomNumberGenerator.GetBytes(PayloadSize);
    }

    [Benchmark(Baseline = true)]
    public ulong FNV1a()
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offsetBasis;
        foreach (var b in (ReadOnlySpan<byte>)_data)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    [Benchmark]
    public ulong XxHash3_64() => XxHash3.HashToUInt64(_data);
}