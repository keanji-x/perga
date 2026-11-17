namespace Perga.TermClient.Tests;

public sealed class ContractSmokeTests
{
    [Fact]
    public void TermClientContracts_ConstructCleanly()
    {
        var req = new TermEnsureRequest("t_1", "/bin/bash", [], "/tmp", new Dictionary<string, string>());
        var observe = new TermObserveRequest("t_1");

        Assert.Equal((ushort)120, req.Cols);
        Assert.Equal((ushort)40, req.Rows);
        Assert.Equal(150, observe.SettleMs);
    }
}
