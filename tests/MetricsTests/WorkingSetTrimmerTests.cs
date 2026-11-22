using System;
using System.Diagnostics;
using TaskbarMonitor;
using Xunit;

namespace MetricsTests;

public sealed class WorkingSetTrimmerTests
{
    [Fact]
    public void TrimWorkingSet_ExecutesWithoutException()
    {
        // Allocate some temporary memory to exercise GC
        byte[] buffer = new byte[1024 * 1024 * 4];
        for (int i = 0; i < buffer.Length; i += 4096)
        {
            buffer[i] = 1;
        }

        long beforeWs = Process.GetCurrentProcess().WorkingSet64;
        var exception = Record.Exception(() => WorkingSetTrimmer.TrimWorkingSet());
        Assert.Null(exception);

        // Buffer goes out of scope and memory compaction runs
        GC.KeepAlive(buffer);
    }

    [Fact]
    public void WorkingSetTrimmer_Lifecycle_CanConstructAndDispose()
    {
        using var trimmer = new WorkingSetTrimmer(TimeSpan.FromSeconds(60));
        var exception = Record.Exception(() => trimmer.Trim());
        Assert.Null(exception);
    }

    [Fact]
    public void WorkingSetTrimmer_MultipleDisposes_AreSafe()
    {
        var trimmer = new WorkingSetTrimmer(TimeSpan.FromSeconds(60));
        trimmer.Dispose();
        var exception = Record.Exception(() => trimmer.Dispose());
        Assert.Null(exception);
    }
}
