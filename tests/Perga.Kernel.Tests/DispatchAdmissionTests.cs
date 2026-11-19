using Perga.Kernel.SystemUpgrade;

namespace Perga.Kernel.Tests;

public sealed class DispatchAdmissionTests
{
    [Fact]
    public async Task DrainWaitsForExistingLeaseAndResumesWaitingAdmission()
    {
        var admission = new DispatchAdmission();
        var active = await admission.EnterAsync(CancellationToken.None);
        admission.SetDraining(true);
        var waiting = admission.EnterAsync(CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, admission.ActiveDispatches);
        active.Dispose();
        active.Dispose();
        Assert.Equal(0, admission.ActiveDispatches);
        Assert.False(waiting.IsCompleted);
        admission.SetDraining(false);
        using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, admission.ActiveDispatches);
    }

    [Fact]
    public async Task CancellationWhileDrainingDoesNotCountAsDispatch()
    {
        var admission = new DispatchAdmission();
        admission.SetDraining(true);
        using var cancellation = new CancellationTokenSource();
        var waiting = admission.EnterAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, admission.ActiveDispatches);
        admission.SetDraining(false);
        using var lease = await admission.EnterAsync(CancellationToken.None);
        Assert.Equal(1, admission.ActiveDispatches);
    }
}
