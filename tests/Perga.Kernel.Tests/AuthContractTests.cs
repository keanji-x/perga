using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class AuthContractTests
{
    [Fact]
    public async Task BootstrapRequiresAuthentication()
    {
        await using var context = new KernelTestContext();
        Assert.True((await context.Kernel.GetBootstrapAsync()).AuthRequired);
    }

    [Fact]
    public async Task HumanPrincipalCarriesUserIdentityAndRetainsOwnerAuthority()
    {
        await using var context = new KernelTestContext();
        var (_, snapshot) = await context.CreateDefaultTrackAsync();
        var owner = new InteropPrincipal.Human("usr_0123456789abcdef0123456789abcdef", "owner");

        Assert.Equal("usr_0123456789abcdef0123456789abcdef", owner.UserId);
        Assert.Equal("owner", owner.Username);
        Assert.Single(await context.Kernel.ListSessionsAsync(snapshot.Track.Id, new(), owner, default));
    }
}
