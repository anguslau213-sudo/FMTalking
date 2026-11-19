using System;

namespace FmPlayerTalk;

// FM can retain a released relationship buffer in a size-class free list and
// later reuse the whole class. Allocate enough physical bytes for that reuse;
// the vector still advertises only its requested capacity.
internal static class FmRelationBufferAllocation
{
    private const int RecordBytes = 16;
    private const int MaximumCapacity = 4096;
    private const int LargestPooledBytes = 4096;
    private static readonly int[] MeasuredClasses = { 144, 160, 192, 224, 256, 288, 320, 384, 448, 512 };

    // Windows heap data and the conservative upper bound above 512 bytes:
    // https://github.com/tozmoz/FMLibreEditor/blob/3000c4de52b69fb31a61bf27eaf0e483a9ddf3a6/profiles/fm27/layout.toml#L490-L496
    // Small relationship buffers are multiples of 16, matching the smaller
    // classes' 8/16-byte steps. Past 4096 bytes FM returns the allocation to CRT.
    internal static int BytesForCapacity(int capacity)
    {
        if (capacity is < 1 or > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        var requested = checked(capacity * RecordBytes);
        if (requested <= MeasuredClasses[0] || requested > LargestPooledBytes)
            return requested;

        foreach (var size in MeasuredClasses)
            if (requested <= size)
                return size;

        var sufficient = MeasuredClasses[^1];
        while (sufficient < requested)
            sufficient = checked(sufficient * 2);
        return sufficient;
    }
}
