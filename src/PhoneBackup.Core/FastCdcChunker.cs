namespace PhoneBackup.Core;

public sealed class FastCdcChunker
{
    public const int MinSize = 256 * 1024;
    public const int AverageSize = 1024 * 1024;
    public const int MaxSize = 4 * 1024 * 1024;

    private static readonly ulong[] Gear = BuildGearTable();
    private const ulong SmallMask = (1UL << 19) - 1;
    private const ulong LargeMask = (1UL << 21) - 1;

    public async IAsyncEnumerable<byte[]> ChunkAsync(
        Stream source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[MaxSize];
        var buffered = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(buffered, MaxSize - buffered), cancellationToken);
            if (read == 0 && buffered == 0)
                yield break;
            buffered += read;

            var cut = FindCut(buffer.AsSpan(0, buffered), read == 0);
            if (cut == 0)
                continue;

            var chunk = buffer.AsSpan(0, cut).ToArray();
            Buffer.BlockCopy(buffer, cut, buffer, 0, buffered - cut);
            buffered -= cut;
            yield return chunk;
        }
    }

    internal static int FindCut(ReadOnlySpan<byte> data, bool endOfStream)
    {
        if (data.Length <= MinSize)
            return endOfStream ? data.Length : 0;

        var limit = Math.Min(data.Length, MaxSize);
        ulong hash = 0;
        var normal = Math.Min(limit, AverageSize);
        for (var i = MinSize; i < normal; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if ((hash & SmallMask) == 0)
                return i + 1;
        }
        for (var i = normal; i < limit; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if ((hash & LargeMask) == 0)
                return i + 1;
        }
        return limit == MaxSize || endOfStream ? limit : 0;
    }

    private static ulong[] BuildGearTable()
    {
        var table = new ulong[256];
        ulong x = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < table.Length; i++)
        {
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            table[i] = x * 0x2545F4914F6CDD1DUL;
        }
        return table;
    }
}

