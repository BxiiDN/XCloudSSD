using System.Security.Cryptography;
using XCloudSSD.Core;
using XCloudSSD.TestSupport;
using XCloudSSD.Transfer;

namespace XCloudSSD.Tests;

public class TransferTests
{
    private static (InMemoryCloudProvider P, MemoryBudget B, ChunkReadCache C) Setup(long budgetBytes, int chunk = 1 << 20)
    {
        var p = new InMemoryCloudProvider();
        var b = new MemoryBudget(budgetBytes);
        var c = new ChunkReadCache(p, b, chunk, 2, 4, TimeSpan.FromMinutes(5));
        return (p, b, c);
    }

    private static byte[] Rand(int n, int seed)
    {
        var data = new byte[n];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void ChunkCache_ReadsAcrossChunkBoundaries_AndReusesRam()
    {
        var (p, b, c) = Setup(64L << 20, chunk: 256 * 1024);
        var data = Rand(3_000_000, 1);
        var item = p.Seed(p.RootId, "f.bin", data);

        var buf = new byte[700_000];
        var n = c.Read(item.Id, "v1", data.Length, 200_000, buf, null, CancellationToken.None);
        Assert.Equal(buf.Length, n);
        Assert.True(buf.AsSpan().SequenceEqual(data.AsSpan(200_000, buf.Length)));

        var calls = p.ReadRangeCalls;
        c.Read(item.Id, "v1", data.Length, 250_000, buf.AsSpan(0, 1000), null, CancellationToken.None);
        Assert.Equal(calls, p.ReadRangeCalls); // served from the RAM buffer
        Assert.True(b.Used <= b.Capacity);
    }

    [Fact]
    public void ChunkCache_RespectsBudget()
    {
        var (p, b, c) = Setup(4L << 20, chunk: 1 << 20);
        var data = Rand(20 << 20, 2);
        var item = p.Seed(p.RootId, "big.bin", data);
        var buf = new byte[1 << 20];
        for (long off = 0; off < data.Length; off += buf.Length)
        {
            c.Read(item.Id, "v", data.Length, off, buf, null, CancellationToken.None);
            Assert.True(buf.AsSpan().SequenceEqual(data.AsSpan((int)off, buf.Length)));
        }
        Assert.True(b.Peak <= b.Capacity, $"peak {b.Peak} > capacity {b.Capacity}");
        Assert.True(c.CachedBytes <= b.Capacity);
    }

    [Fact]
    public void WriteSession_StreamsLargeSequentialFile_WithBoundedRam()
    {
        var (p, b, c) = Setup(24L << 20);
        var opts = new WriteOptions(UploadChunkBytes: 4 << 20, StreamingThresholdBytes: 8L << 20);
        var s = new WriteSession(p, b, c, opts, null, null, 0);
        s.BindStreamTarget(new CommitTarget(null, p.RootId, "stream.bin", 0x20, DateTimeOffset.UtcNow));

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var block = new byte[1 << 20];
        var rnd = new Random(3);
        const int totalMb = 160; // far more than the 24 MiB RAM budget
        for (var i = 0; i < totalMb; i++)
        {
            rnd.NextBytes(block);
            md5.AppendData(block);
            s.Write((long)i << 20, block, CancellationToken.None);
        }
        Assert.True(s.StreamedBytes > 0, "data should have been streamed while writing");
        var item = s.Commit(new CommitTarget(null, p.RootId, "stream.bin", 0x20, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal((long)totalMb << 20, item!.Size);
        Assert.Equal(Convert.ToHexStringLower(md5.GetHashAndReset()), item.Md5);
        Assert.True(b.Peak <= b.Capacity, $"RAM peak {b.Peak} exceeded budget {b.Capacity}");
        Assert.Equal(0, b.Used - c.CachedBytes); // write pages released after commit
    }

    [Fact]
    public void WriteSession_RandomWritesIntoExistingFile_MergeWithCloudContent()
    {
        var (p, b, c) = Setup(64L << 20);
        var data = Rand(5_000_000, 4);
        var item = p.Seed(p.RootId, "save.dat", data);
        var s = new WriteSession(p, b, c, new WriteOptions(1 << 20, 8L << 20), item.Id, item.Md5, item.Size);

        var expected = (byte[])data.Clone();
        var rnd = new Random(5);
        for (var i = 0; i < 50; i++)
        {
            var off = rnd.Next(0, data.Length - 5000);
            var patch = Rand(rnd.Next(1, 5000), i);
            patch.CopyTo(expected, off);
            s.Write(off, patch, CancellationToken.None);
        }
        // Read-your-writes before commit.
        var check = new byte[expected.Length];
        Assert.Equal(expected.Length, s.Read(0, check, null, CancellationToken.None));
        Assert.True(check.AsSpan().SequenceEqual(expected));

        var committed = s.Commit(new CommitTarget(item.Id, null, null, 0x20, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.NotNull(committed);
        Assert.True(p.ContentAt("save.dat")!.AsSpan().SequenceEqual(expected));
        Assert.False(s.Dirty);
    }

    [Fact]
    public void WriteSession_TruncateAndExtend_ProducesZeros()
    {
        var (p, b, c) = Setup(16L << 20);
        var data = Rand(300_000, 6);
        var item = p.Seed(p.RootId, "t.bin", data);
        var s = new WriteSession(p, b, c, new WriteOptions(1 << 20, 8L << 20), item.Id, item.Md5, item.Size);
        s.SetSize(100_000);
        s.SetSize(200_000);
        s.Commit(new CommitTarget(item.Id, null, null, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        var result = p.ContentAt("t.bin")!;
        Assert.Equal(200_000, result.Length);
        Assert.True(result.AsSpan(0, 100_000).SequenceEqual(data.AsSpan(0, 100_000)));
        Assert.All(result.Skip(100_000), x => Assert.Equal(0, x));
    }

    [Fact]
    public void WriteSession_WriteBelowStreamedData_FailsClearly()
    {
        var (p, b, c) = Setup(32L << 20);
        var s = new WriteSession(p, b, c, new WriteOptions(1 << 20, 2L << 20), null, null, 0);
        s.BindStreamTarget(new CommitTarget(null, p.RootId, "x.bin", 0, DateTimeOffset.UtcNow));
        var block = new byte[1 << 20];
        for (var i = 0; i < 8; i++) s.Write((long)i << 20, block, CancellationToken.None);
        Assert.True(s.StreamedBytes > 0);
        var ex = Assert.Throws<WriteSessionException>(() => s.Write(0, new byte[10], CancellationToken.None));
        Assert.Equal(WriteFailure.BelowStreamedData, ex.Failure);
    }

    [Fact]
    public void WriteSession_RandomWritesBeyondBudget_FailWithoutDiskFallback()
    {
        var (p, b, c) = Setup(4L << 20);
        var s = new WriteSession(p, b, c, new WriteOptions(1 << 20, 64L << 20), null, null, 0);
        var block = new byte[WriteSession.PageSize];
        var ex = Assert.Throws<WriteSessionException>(() =>
        {
            // Backwards writes can never be streamed, so they must stay in RAM.
            for (var i = 200; i >= 0; i--) s.Write((long)i * WriteSession.PageSize, block, CancellationToken.None);
        });
        Assert.Equal(WriteFailure.BufferExhausted, ex.Failure);
        Assert.True(b.Peak <= b.Capacity);
    }

    [Fact]
    public void WriteSession_TransientFailure_KeepsDataForRetry()
    {
        var (p, b, c) = Setup(32L << 20);
        var s = new WriteSession(p, b, c, new WriteOptions(1 << 20, 64L << 20), null, null, 0);
        var data = Rand(3_000_000, 7);
        s.Write(0, data, CancellationToken.None);
        var target = new CommitTarget(null, p.RootId, "retry.bin", 0, DateTimeOffset.UtcNow);

        p.FailNextChunks = 1;
        Assert.Throws<CloudException>(() => s.Commit(target, CancellationToken.None));
        Assert.True(s.Dirty);

        var item = s.Commit(target, CancellationToken.None);
        Assert.NotNull(item);
        Assert.True(p.ContentAt("retry.bin")!.AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void WriteSession_Offline_ThenOnline_Commits()
    {
        var (p, b, c) = Setup(32L << 20);
        var s = new WriteSession(p, b, c, new WriteOptions(1 << 20, 64L << 20), null, null, 0);
        var data = Rand(1_000_000, 8);
        s.Write(0, data, CancellationToken.None);
        var target = new CommitTarget(null, p.RootId, "off.bin", 0, DateTimeOffset.UtcNow);
        p.Offline = true;
        var ex = Assert.Throws<CloudException>(() => s.Commit(target, CancellationToken.None));
        Assert.Equal(CloudErrorKind.Offline, ex.Kind);
        Assert.True(s.Dirty);
        p.Offline = false;
        Assert.NotNull(s.Commit(target, CancellationToken.None));
        Assert.True(p.ContentAt("off.bin")!.AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void WriteSession_NewEmptyFile_IsCreatedOnCommit()
    {
        var (p, b, c) = Setup(8L << 20);
        var s = new WriteSession(p, b, c, new WriteOptions(1 << 20, 64L << 20), null, null, 0);
        Assert.True(s.Dirty);
        var item = s.Commit(new CommitTarget(null, p.RootId, "empty.txt", 0, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.NotNull(item);
        Assert.Equal(0, item!.Size);
        Assert.True(p.Exists("empty.txt"));
    }

    [Fact]
    public void MemoryBudget_ReclaimsReadBuffersForWrites()
    {
        var (p, b, c) = Setup(4L << 20, chunk: 1 << 20);
        var data = Rand(4 << 20, 9);
        var item = p.Seed(p.RootId, "r.bin", data);
        var buf = new byte[4 << 20];
        c.Read(item.Id, "v", data.Length, 0, buf, null, CancellationToken.None);
        Assert.True(c.CachedBytes > 0);
        Assert.True(b.TryReserve(3L << 20)); // forces eviction of cached read chunks
        Assert.True(b.Used <= b.Capacity);
    }
}
