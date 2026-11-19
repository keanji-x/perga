using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Host.SystemUpgrade;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.Http;

public static class SystemEndpoints
{
    public static void MapPergaSystem(this WebApplication app)
    {
        app.MapGet("/api/v1/system/info", async (HttpContext ctx, IPergaKernel kernel, ISystemUpgradeStore store, ReleaseSnapshotManager releases) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var identity = releases.Identity;
            return Results.Ok(new SystemInfoDto(identity.ReleaseId, identity.WebBuildId, identity.HostBuildId,
                identity.BridgeBuildId, await store.GetSchemaVersionAsync(ctx.RequestAborted),
                kernel.DispatchAdmission.Draining, kernel.DispatchAdmission.ActiveDispatches));
        });
        app.MapPost("/api/v1/system/drain", (HttpContext ctx, SystemDrainRequest request, IPergaKernel kernel, SystemManagementAuthorization authorization) =>
        {
            if (!authorization.Verify(ctx)) return HttpErrorHandling.Forbidden("system_management_forbidden", "Installation owner authorization is required.");
            kernel.DispatchAdmission.SetDraining(request.Draining);
            return Results.Json(new SystemDrainResponse(kernel.DispatchAdmission.Draining, kernel.DispatchAdmission.ActiveDispatches), statusCode: 202);
        });
        app.MapPost("/api/v1/system/reload", (HttpContext ctx, SystemReloadRequest request, ReleaseSnapshotManager releases, SystemManagementAuthorization authorization) =>
        {
            if (!authorization.Verify(ctx)) return HttpErrorHandling.Forbidden("system_management_forbidden", "Installation owner authorization is required.");
            return Results.Ok(releases.Reload(request.ReleaseId, request.WebBuildId));
        });
    }

}
