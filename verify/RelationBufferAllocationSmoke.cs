using System;
using FmPlayerTalk;

public static class RelationBufferAllocationSmoke
{
    public static string Run()
    {
        var older = FmNativeLayout.Steam2701;
        var newer = FmNativeLayout.Steam2710;
        Require(FmNativeLayout.Match(older.Timestamp, older.ImageSize) == older &&
            FmNativeLayout.Match(newer.Timestamp, newer.ImageSize) == newer &&
            FmNativeLayout.Match(older.Timestamp, newer.ImageSize) == null &&
            FmNativeLayout.Match(newer.Timestamp + 1, newer.ImageSize) == null &&
            older.RelationsInitializer == null && newer.RelationsInitializer != null,
            "Native layout selection must reject unknown builds and distinguish empty-relation support.");

        // Capacities around the measured native class boundaries, including
        // non-power-of-two capacities found after a vector has been reloaded.
        foreach (var (capacity, expectedBytes) in new[]
        {
            (1, 16), (3, 48), (8, 128), (9, 144), (10, 160), (11, 192),
            (13, 224), (15, 256), (17, 288), (19, 320), (20, 320),
            (21, 384), (22, 384), (24, 384), (25, 448), (28, 448),
            (29, 512), (32, 512), (33, 1024), (34, 1024), (64, 1024),
            (65, 2048), (128, 2048), (129, 4096), (256, 4096),
            (257, 4112), (4096, 65536)
        })
        {
            Require(FmRelationBufferAllocation.BytesForCapacity(capacity) == expectedBytes,
                $"Relationship capacity {capacity} must physically reserve {expectedBytes} bytes.");
        }

        // This is the concrete unsafe old case: 22 records advertise 352 bytes,
        // but the native allocator may hand its block out again as a 384-byte
        // class. Preserve the vector capacity while reserving all 384 bytes.
        var advertisedBytes = 22 * 16;
        var allocatedBytes = FmRelationBufferAllocation.BytesForCapacity(22);
        Require(advertisedBytes == 352 && allocatedBytes >= 384,
            "A native class reuse must fit without extending the advertised vector capacity.");

        for (var capacity = 1; capacity <= 4096; capacity++)
        {
            var requested = capacity * 16;
            var allocated = FmRelationBufferAllocation.BytesForCapacity(capacity);
            Require(allocated >= requested && allocated % 16 == 0,
                "Every supported relation capacity fits in its physical allocation.");
            Require(requested <= 4096 ? allocated <= 4096 : allocated == requested,
                "Pooled allocations stay within their range; larger CRT allocations keep their exact size.");
        }

        foreach (var invalid in new[] { int.MinValue, -1, 0, 4097, int.MaxValue })
        {
            try
            {
                FmRelationBufferAllocation.BytesForCapacity(invalid);
                throw new InvalidOperationException("Invalid relationship capacity was accepted.");
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return "PASS: native build identification, empty-relation support, relationship buffer sizing and invalid input guards.";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
