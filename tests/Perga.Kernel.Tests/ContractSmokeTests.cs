using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

public sealed class ContractSmokeTests
{
    [Fact]
    public void KernelPorts_ConstructCleanly()
    {
        using var json = System.Text.Json.JsonDocument.Parse("{}");
        var elem = json.RootElement.Clone();
        var track = new TrackDto(
            "trk_1",
            "area_1",
            "Demo",
            "/tmp",
            "direct",
            elem,
            "ses_1",
            null,
            elem,
            [],
            null,
            null,
            0,
            1000,
            1000);
        var snapshot = new TrackAggregateSnapshot(
            track,
            new ReportDocument(0, "", "", []),
            [],
            [],
            [],
            null);

        Assert.Equal("perga", PergaKernelAssembly.ServiceName);
        Assert.Equal("trk_1", snapshot.Track.Id);
    }
}
