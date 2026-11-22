using Perga.Contracts;
using Perga.Host.Auth;
using Perga.Host.SystemUpgrade;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.Http;

public static class SystemEndpoints
{
    public static void MapPergaSystem(this WebApplication app)
    {
        var system = app.MapGroup("/api/v1/system").WithMetadata(new RequestOriginGuard.BrowserWriteApi());
        system.MapGet("/info", async (HttpContext ctx, IPergaKernel kernel, ISystemUpgradeStore store, ReleaseSnapshotManager releases) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var identity = releases.Identity;
            return Results.Ok(new SystemInfoDto(identity.ReleaseId, identity.WebBuildId, identity.HostBuildId,
                identity.BridgeBuildId, await store.GetSchemaVersionAsync(ctx.RequestAborted),
                kernel.DispatchAdmission.Draining, kernel.DispatchAdmission.ActiveDispatches));
        }).RequireAuthorization(PergaAuthPolicies.UserOrManagement);
        system.MapPost("/drain", (SystemDrainRequest request, IPergaKernel kernel) =>
        {
            kernel.DispatchAdmission.SetDraining(request.Draining);
            return Results.Json(new SystemDrainResponse(kernel.DispatchAdmission.Draining, kernel.DispatchAdmission.ActiveDispatches), statusCode: 202);
        }).RequireAuthorization(PergaAuthPolicies.SystemManagementOnly);
        system.MapPost("/reload", (SystemReloadRequest request, ReleaseSnapshotManager releases) =>
        {
            return Results.Ok(releases.Reload(request.ReleaseId, request.WebBuildId));
        }).RequireAuthorization(PergaAuthPolicies.SystemManagementOnly);
        system.MapGet("/auth-status", async (HttpContext ctx, IAuthStore store, TimeProvider time) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await store.GetAuthStatusAsync(time.GetUtcNow().ToUnixTimeMilliseconds(), ctx.RequestAborted));
        }).RequireAuthorization(PergaAuthPolicies.SystemManagementOnly);
    }
}
