// SPDX-FileCopyrightText: 2026 Cedric Moulard / Kraftr
// SPDX-License-Identifier: MIT

namespace BillingService;

public static class MemoryLeakSimulator
{
    private static readonly List<byte[]> Retained = new();
    private const int ChunkBytes = 1024 * 1024; // 1 MiB

    public static void Allocate(int multiplier)
    {
        if (multiplier <= 0) return;
        for (var i = 0; i < multiplier; i++)
        {
            Retained.Add(new byte[ChunkBytes]);
        }
    }

    public static int RetainedChunks => Retained.Count;
}
