namespace Perga.VcsKit.Tests;

public sealed class ContractSmokeTests
{
    [Fact]
    public void VcsKitContracts_ConstructCleanly()
    {
        var req = new WorkspaceProvisionRequest("trk_1", "/repo", "/workspaces", ["target", "node_modules"]);
        var snap = new RevisionSnapshot(VcsBackendKind.JjColocated, "kkmpptxz", "abc123", "op_1", false, []);

        Assert.Equal("trk_1", req.TrackId);
        Assert.Equal(VcsBackendKind.JjColocated, snap.Kind);
    }
}
