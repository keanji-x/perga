using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Perga.Host.Configuration;
using Perga.Host.Terminals;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;
using Perga.TermClient;

namespace Perga.Host.Lifecycle;

public sealed class PergaBootHostedService : IHostedService
{
    private readonly ITrackStore _store;
    private readonly IPergaKernel _kernel;
    private readonly ITerminalBridgeClient _terminalClient;
    private readonly PergaHostOptions _options;
    private readonly ILogger<PergaBootHostedService> _logger;
    private readonly IExecutionTargetManager? _targetManager;

    public PergaBootHostedService(
        ITrackStore store,
        IPergaKernel kernel,
        ITerminalBridgeClient terminalClient,
        PergaHostOptions options,
        ILogger<PergaBootHostedService> logger,
        IExecutionTargetManager? targetManager = null)
    {
        _store = store;
        _kernel = kernel;
        _terminalClient = terminalClient;
        _options = options;
        _logger = logger;
        _targetManager = targetManager;
    }

    public BootReconcileResult? LastReconcileResult { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_store is SqliteTrackStore sqliteStore)
        {
            await sqliteStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_targetManager is not null)
        {
            await _targetManager.LoadPersistedTargetsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_options.AutoManageTerminalBridge &&
            _options.EagerStartTerminalBridge &&
            _terminalClient is ManagedTerminalBridgeClient managedBridge)
        {
            await managedBridge.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await _kernel.ReconcileOnBootAsync(cancellationToken).ConfigureAwait(false);
        LastReconcileResult = result;

        if (result.AffectedTrackIds.Count > 0)
        {
            _logger.LogInformation(
                "Perga boot reconciliation completed: {AffectedTracks} tracks reconciled, {WokenTracks} tracks woken.",
                result.AffectedTrackIds.Count,
                result.WokenTracks);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
