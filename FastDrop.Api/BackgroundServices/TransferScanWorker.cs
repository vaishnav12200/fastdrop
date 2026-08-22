using FastDrop.Application.Common.Interfaces;
using FastDrop.Domain.Enums;

namespace FastDrop.Api.BackgroundServices;

/// <summary>Fails closed: only a clean scanner result permits publishing a link.</summary>
public sealed class TransferScanWorker : BackgroundService
{
    private const int BatchSize = 4;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorage _storage;
    private readonly IFileSecurityScanner _scanner;
    private readonly IDistributedLockProvider _locks;
    private readonly ILogger<TransferScanWorker> _logger;

    public TransferScanWorker(IServiceScopeFactory scopeFactory, IFileStorage storage, IFileSecurityScanner scanner,
        IDistributedLockProvider locks, ILogger<TransferScanWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _storage = storage;
        _scanner = scanner;
        _locks = locks;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do { await ScanPendingTransfersAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ScanPendingTransfersAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITransferRepository>();
        var pending = await repository.GetByStatusAsync(TransferStatus.Scanning, BatchSize, cancellationToken);

        foreach (var transfer in pending)
        {
            try
            {
                await using var scanLock = await _locks.TryAcquireLockAsync($"TransferScan_{transfer.Id}",
                    TimeSpan.FromMinutes(30), TimeSpan.Zero, cancellationToken);
                if (scanLock is null) continue;

                await using var content = await _storage.OpenFinalFileAsync(transfer.Id, transfer.File.TotalChunks, cancellationToken);
                var result = await _scanner.ScanAsync(content, cancellationToken);
                if (result.Verdict == FileScanVerdict.Unavailable) continue;

                var current = await repository.GetByIdAsync(transfer.Id, cancellationToken);
                if (current?.Status != TransferStatus.Scanning) continue;

                if (result.Verdict == FileScanVerdict.Clean)
                {
                    current.MarkClean();
                    _logger.LogInformation("Transfer {TransferId} passed malware scanning.", current.Id);
                }
                else
                {
                    current.Block();
                    _logger.LogWarning("Transfer {TransferId} was blocked: {Reason}", current.Id, result.Detail);
                }

                await repository.SaveChangesAsync(cancellationToken);
                if (result.Verdict == FileScanVerdict.ThreatFound)
                    await _storage.DeleteTransferAsync(current.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken scan must never make a file shareable. Log and retry
                // it later while the transfer remains in its Scanning state.
                _logger.LogError(ex, "Failed to scan transfer {TransferId}; leaving it quarantined.", transfer.Id);
            }
        }
    }
}
