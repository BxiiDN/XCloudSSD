using System.Security.Cryptography;
using XCloudSSD.Core;
using XCloudSSD.Metadata;
using XCloudSSD.TestSupport;
using XCloudSSD.Transfer;

namespace XCloudSSD.Tests;

/// <summary>Write patterns seen during Steam/Epic installs: unaligned blocks, local reordering, many files at once.</summary>
public class GameInstallWriteTests
{
    private static (InMemoryCloudProvider P, MemoryBudget B, ChunkReadCache C) Setup(long budgetBytes)
    {
        var p = new InMemoryCloudProvider();
        var b = new MemoryBudget(budgetBytes);
        var c = new ChunkReadCache(p, b, 1 << 20, 2, 4, TimeSpan.FromMinutes(5));
        return (p, b, c);
    }

    [Fact]
    public void UnalignedOutOfOrderBlocks_StillStream_WithBoundedRam()
    {
        // 48 MiB of RAM for a 192 MiB file whose 1 MiB blocks arrive split at odd offsets and shuffled
        // inside 4 MiB windows (parallel chunk downloads). Old builds stalled here: pages written in two
        // halves never counted as complete, nothing streamed and the buffer filled up.
        var (p, b, c) = Setup(48L << 20);
        var s = new WriteSession(p, b, c, new WriteOptions(4 << 20, 8L << 20, true, 5), null, null, 0);
        s.BindStreamTarget(new CommitTarget(null, p.RootId, "pak0.cpk", 0x20, DateTimeOffset.UtcNow));

        const int totalMb = 192;
        var data = new byte[totalMb << 20];
        new Random(11).NextBytes(data);
        var rnd = new Random(12);
        for (var window = 0; window < totalMb; window += 4)
        {
            var pieces = new List<(int Off, int Len)>();
            for (var mb = window; mb < window + 4; mb++)
            {
                var start = mb << 20;
                var cut = 1 + rnd.Next((1 << 20) - 2); // never page aligned
                pieces.Add((start, cut));
                pieces.Add((start + cut, (1 << 20) - cut));
            }
            foreach (var (off, len) in pieces.OrderBy(_ => rnd.Next()))
                s.Write(off, data.AsSpan(off, len), CancellationToken.None);
        }
        Assert.True(s.StreamedBytes > (totalMb - 16L) << 20, $"only {s.StreamedBytes} bytes streamed");
        var item = s.Commit(new CommitTarget(null, p.RootId, "pak0.cpk", 0x20, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.NotNull(item);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(data)), item!.Md5);
        Assert.True(b.Peak <= b.Capacity, $"RAM peak {b.Peak} > {b.Capacity}");
    }

    [Fact]
    public void ReservedFile_ThenFilledInOrder_StreamsAndKeepsSize()
    {
        // Steam: set the final size (sparse, instant), close, reopen and write the content.
        var (p, b, c) = Setup(32L << 20);
        var reserve = new WriteSession(p, b, c, new WriteOptions(4 << 20, 8L << 20), null, null, 0);
        reserve.SetSize(96L << 20);
        var reserved = reserve.Commit(new CommitTarget(null, p.RootId, "data.pak", 0x20, DateTimeOffset.UtcNow), CancellationToken.None)!;
        Assert.Equal(96L << 20, reserved.Size);
        Assert.Equal(0, reserved.ContentLength);

        var data = new byte[96 << 20];
        new Random(13).NextBytes(data);
        var s = new WriteSession(p, b, c, new WriteOptions(4 << 20, 8L << 20, true, 5), reserved.Id, reserved.Md5,
            reserved.ContentLength, reserved.Size);
        s.BindStreamTarget(new CommitTarget(reserved.Id, null, null, 0x20, DateTimeOffset.UtcNow));
        for (var off = 0; off < data.Length; off += 1 << 20)
            s.Write(off, data.AsSpan(off, 1 << 20), CancellationToken.None);
        Assert.True(s.StreamedBytes > 0, "a reserved (empty) file must stream while it is filled");
        var item = s.Commit(new CommitTarget(reserved.Id, null, null, 0x20, DateTimeOffset.UtcNow), CancellationToken.None)!;
        Assert.Equal(96L << 20, item.Size);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(data)), item.Md5);
        Assert.True(b.Peak <= b.Capacity);
    }

    [Fact]
    public void FullBuffer_WaitsForOtherFiles_InsteadOfFailing()
    {
        var (p, b, c) = Setup(8L << 20);
        // File A holds 6 MiB that cannot be uploaded yet (no destination bound, like a file still being created).
        var a = new WriteSession(p, b, c, new WriteOptions(1 << 20, 64L << 20), null, null, 0);
        a.Write(0, new byte[6 << 20].Select((_, i) => (byte)(i | 1)).ToArray(), CancellationToken.None);

        var bSession = new WriteSession(p, b, c, new WriteOptions(1 << 20, 64L << 20, true, 10), null, null, 0);
        var payload = Enumerable.Range(0, 4 << 20).Select(i => (byte)(i * 7 + 1)).ToArray();
        var writer = Task.Run(() => bSession.Write(0, payload, CancellationToken.None));
        Thread.Sleep(400);
        Assert.False(writer.IsCompleted, "the write should be waiting for RAM, not failing");
        a.Commit(new CommitTarget(null, p.RootId, "a.bin", 0x20, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.True(writer.Wait(TimeSpan.FromSeconds(10)), "the write should continue once A was uploaded");
        Assert.True(writer.IsCompletedSuccessfully);
        bSession.Commit(new CommitTarget(null, p.RootId, "b.bin", 0x20, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.True(p.ContentAt("b.bin")!.AsSpan().SequenceEqual(payload));
        Assert.True(b.Peak <= b.Capacity);
    }

    [Fact]
    public void FullBuffer_WithoutWait_FailsClearly_AndNeverExceedsBudget()
    {
        var (p, b, c) = Setup(8L << 20);
        var a = new WriteSession(p, b, c, new WriteOptions(1 << 20, 64L << 20), null, null, 0);
        a.Write(0, Enumerable.Repeat((byte)1, 6 << 20).ToArray(), CancellationToken.None);
        var other = new WriteSession(p, b, c, new WriteOptions(1 << 20, 64L << 20), null, null, 0);
        var ex = Assert.Throws<WriteSessionException>(() => other.Write(0, Enumerable.Repeat((byte)2, 4 << 20).ToArray(), CancellationToken.None));
        Assert.Equal(WriteFailure.BufferExhausted, ex.Failure);
        Assert.True(b.Used <= b.Capacity);
        a.Discard();
        other.Discard();
        Assert.Equal(0, b.Used - c.CachedBytes);
    }

    [Fact]
    public void SparseSizeStore_PersistsSizesLocally()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xcs-sparse-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var s = new SparseSizeStore(dir, "t-1"))
            {
                s.Set("123", 5L << 30);
                s.Set("456", 10);
                s.Set("456", null);
            }
            using var again = new SparseSizeStore(dir, "t-1");
            Assert.Equal(5L << 30, again.Get("123"));
            Assert.Null(again.Get("456"));
            using var otherAccount = new SparseSizeStore(dir, "t-2");
            Assert.Null(otherAccount.Get("123"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
