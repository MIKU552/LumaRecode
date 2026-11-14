using System.Runtime.CompilerServices;

namespace VideoConverter.Core;

/// <summary>Budgets for metadata and indexes, independent of compressed video size and resolution.</summary>
internal static class MediaResourceLimits
{
    internal const int TextOutputBytes = 64 * 1024 * 1024;
    internal const int LineCharacters = 65536;
    // Reserve room for both the live index and its replacement/snapshot. This
    // covers more than 24 hours at 120 fps with the current 24-byte records.
    internal const long IndexBytes = 512L * 1024 * 1024;
}

/// <summary>Grows a compact unmanaged index within a budget that includes a second array.</summary>
/// <remarks>No capacity is inferred from untrusted duration or frame-count metadata.</remarks>
internal sealed class BoundedMediaIndex<T> where T : unmanaged
{
    private readonly List<T> items = [];
    private readonly long maximumBytes;
    private readonly int maximumCount;
    private static readonly int itemBytes = Unsafe.SizeOf<T>();
    private static readonly int arrayOverhead = 3 * IntPtr.Size;

    internal BoundedMediaIndex(long maximumBytes = MediaResourceLimits.IndexBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        this.maximumBytes = maximumBytes;
        maximumCount = (int)Math.Min(Array.MaxLength, Math.Max(0, (maximumBytes / 2 - arrayOverhead) / itemBytes));
    }

    internal int Count => items.Count;
    internal T this[int index] => items[index];

    internal void Add(T value)
    {
        if (items.Count == maximumCount)
        {
            throw Limit(maximumBytes);
        }
        if (items.Count == items.Capacity)
        {
            // Set Capacity explicitly: List's implicit doubling could exceed
            // the budget even while the retained element count is still small.
            int capacity = (int)Math.Min(maximumCount, Math.Max(256L, (long)items.Capacity * 2));
            items.Capacity = capacity;
        }
        items.Add(value);
    }

    internal void Sort(Comparison<T> comparison) => items.Sort(comparison);
    internal T[] Snapshot() => items.ToArray();

    internal static T[] CopySnapshot(IReadOnlyList<T> source)
    {
        if ((long)source.Count * itemBytes + arrayOverhead > MediaResourceLimits.IndexBytes / 2)
        {
            throw Limit(MediaResourceLimits.IndexBytes);
        }
        return source.ToArray();
    }

    private static InvalidDataException Limit(long bytes) => new(
        $"逐帧索引超过 {bytes / (1024 * 1024)} MiB 的内存预算，已停止读取。");
}
