using System.Text.Json;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.SystemUpgrade;

public sealed class UpgradeReceiptSource(PergaHostOptions options) : ISystemUpgradeReceiptSource
{
    public async Task<SystemUpgradeReceipt?> ReadAsync(CancellationToken ct)
    {
        if (options.InstallationRoot is null)
        {
            return null;
        }
        var path = Path.Combine(options.InstallationRoot, "runtime", "upgrade-result.json");
        if (!File.Exists(path))
        {
            return null;
        }
        var info = new FileInfo(path);
        if (info.Length > 1024 * 1024 || info.LinkTarget is not null)
        {
            throw KernelException.InvalidArgument("Invalid upgrade receipt file.");
        }
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new InvalidOperationException("Input must be a regular file.");
        var receipt = JsonSerializer.Deserialize<SystemUpgradeReceipt>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw KernelException.InvalidArgument("Invalid upgrade receipt.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(receipt.JobId ?? "", @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z") ||
            receipt.Status is not ("succeeded" or "rolled-back" or "failed" or "recovery-required") ||
            receipt.Changed is null || receipt.Changed.Any(c => c is not ("host" or "web" or "templates" or "termBridge")) ||
            !double.IsFinite(receipt.FinishedAt) || receipt.FinishedAt <= 0)
            throw KernelException.InvalidArgument("Invalid upgrade receipt fields.");
        return receipt;
    }
}

public sealed class UpgradeReceiptHostedService(Perga.Kernel.IPergaKernel kernel,
    ILogger<UpgradeReceiptHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await kernel.UpgradeReceipts.ImportAsync(stoppingToken).ConfigureAwait(false);
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Upgrade receipt import failed; will retry.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
