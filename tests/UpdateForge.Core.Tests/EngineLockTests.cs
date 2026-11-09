using UpdateForge.Infrastructure;
using Xunit;

namespace UpdateForge.Core.Tests;

public class EngineLockTests
{
    private static MutexEngineLock Lock() => new($@"Local\UpdateForge.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task Release_from_a_different_thread_than_acquire_works()
    {
        // Regression: the real engine acquires, awaits, and resumes on another thread; a Mutex threw here.
        var l = Lock();
        var held = l.TryAcquire();
        Assert.NotNull(held);
        await Task.Run(() => held!.Dispose(), TestContext.Current.CancellationToken);
        using var again = l.TryAcquire();
        Assert.NotNull(again);
    }

    [Fact]
    public void Second_acquire_is_refused_while_held_and_allowed_after_release()
    {
        var name = $@"Local\UpdateForge.Tests.{Guid.NewGuid():N}";
        var a = new MutexEngineLock(name);
        var b = new MutexEngineLock(name); // simulates another process: separate handle, same kernel object
        var first = a.TryAcquire();
        Assert.NotNull(first);
        Assert.Null(b.TryAcquire());
        first!.Dispose();
        using var second = b.TryAcquire();
        Assert.NotNull(second);
    }

    [Fact]
    public void Double_dispose_does_not_over_release()
    {
        var l = Lock();
        var first = l.TryAcquire();
        first!.Dispose();
        first.Dispose();
        using var one = l.TryAcquire();
        Assert.NotNull(one);
        Assert.Null(l.TryAcquire()); // still exclusive: the count was not pushed above 1
    }
}
