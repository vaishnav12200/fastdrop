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
        _logger.LogInformation("TransferScanWorker started with scanner {ScannerProvider}.", _scanner.GetType().Name);
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
            if (transfer.NextScanAttemptAt is { } retryAt && retryAt > DateTimeOffset.UtcNow)
                continue;

            try
            {
                await using var scanLock = await _locks.TryAcquireLockAsync($"TransferScan_{transfer.Id}",
                    TimeSpan.FromMinutes(30), TimeSpan.Zero, cancellationToken);
                if (scanLock is null) continue;

                var current = await repository.GetByIdAsync(transfer.Id, cancellationToken);
                if (current?.Status != TransferStatus.Scanning) continue;

                FileScanResult result;
                if (!string.IsNullOrWhiteSpace(current.ScannerReference))
                {
                    _logger.LogInformation("Polling malware scan for transfer {TransferId}.", current.Id);
                    result = await _scanner.GetResultAsync(current.ScannerReference, cancellationToken);
                }
                else
                {
                    _logger.LogInformation("Starting malware scan for transfer {TransferId}.", current.Id);
                    await using var content = await _storage.OpenFinalFileAsync(current.Id, current.File.TotalChunks, cancellationToken);
                    result = await _scanner.SubmitAsync(
                        new FileScanRequest(current.File.OriginalFileName, current.File.Size), content, cancellationToken);
                }

                if (result.Verdict == FileScanVerdict.Pending)
                {
                    if (!string.IsNullOrWhiteSpace(result.ScanReference))
                    {
                        current.RecordPendingScan(result.ScanReference, DateTimeOffset.UtcNow);
                        _logger.LogInformation("Malware scan for transfer {TransferId} is pending; reference {ScanReference} recorded.", current.Id, result.ScanReference);
                    }
                    else
                    {
                        current.DeferScan(DateTimeOffset.UtcNow);
                        _logger.LogWarning("Scanner returned a pending result without a reference for transfer {TransferId}; retry scheduled for {RetryAt}.", current.Id, current.NextScanAttemptAt);
                    }
                    await repository.SaveChangesAsync(cancellationToken);
                    continue;
                }

                if (result.Verdict == FileScanVerdict.Unavailable)
                {
                    current.DeferScan(DateTimeOffset.UtcNow);
                    await repository.SaveChangesAsync(cancellationToken);
                    _logger.LogWarning("Malware scanner is temporarily unavailable for transfer {TransferId}; retry scheduled for {RetryAt}. {Reason}", current.Id, current.NextScanAttemptAt, result.Detail);
                    continue;
                }

                if (result.Verdict == FileScanVerdict.Clean)
                {
                    current.MarkClean();
                    _logger.LogInformation("Transfer {TransferId} passed malware scanning.", current.Id);
                }
                else // ThreatFound and Rejected are both unsafe to share.
                {
                    current.Block();
                    if (result.Verdict == FileScanVerdict.ThreatFound)
                        _logger.LogWarning("Malware detected in transfer {TransferId}; blocking and deleting chunks. {Reason}", current.Id, result.Detail);
                    else
                        _logger.LogWarning("Scanner rejected transfer {TransferId}; blocking and deleting chunks. {Reason}", current.Id, result.Detail);
                }

                await repository.SaveChangesAsync(cancellationToken);
                if (result.Verdict != FileScanVerdict.Clean)
                    await _storage.DeleteTransferAsync(current.Id, cancellationToken);
            }
            catch (FileNotFoundException ex)
            {
                await FailTransferWithMissingChunksAsync(repository, transfer.Id, ex, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken scan must never make a file shareable. Log and retry
                // it later while the transfer remains in its Scanning state.
                _logger.LogError(ex, "Failed to scan transfer {TransferId}; leaving it quarantined.", transfer.Id);
                await ScheduleRetryAfterWorkerFailureAsync(repository, transfer.Id, cancellationToken);
            }
        }
    }

    private async Task FailTransferWithMissingChunksAsync(ITransferRepository repository, Guid transferId,
        FileNotFoundException exception, CancellationToken cancellationToken)
    {
        try
        {
            var transfer = await repository.GetByIdAsync(transferId, cancellationToken);
            if (transfer?.Status != TransferStatus.Scanning)
                return;

            _logger.LogError("Required chunk is missing for transfer {TransferId}: {Message}. Marking the transfer Failed; it will not be retried or shared.", transferId, exception.Message);
            transfer.MarkFailed();
            await repository.SaveChangesAsync(cancellationToken);

            try
            {
                await _storage.DeleteTransferAsync(transferId, cancellationToken);
                _logger.LogInformation("Deleted remaining storage for failed transfer {TransferId}.", transferId);
            }
            catch (Exception deleteException) when (deleteException is not OperationCanceledException)
            {
                // The terminal database state is already persisted. Cleanup can
                // make another best-effort deletion attempt after expiration.
                _logger.LogError(deleteException, "Failed to delete remaining storage for failed transfer {TransferId}.", transferId);
            }
        }
        catch (Exception handlingException) when (handlingException is not OperationCanceledException)
        {
            _logger.LogError(handlingException, "Could not record missing-chunk failure for transfer {TransferId}.", transferId);
        }
    }

    private async Task ScheduleRetryAfterWorkerFailureAsync(ITransferRepository repository, Guid transferId,
        CancellationToken cancellationToken)
    {
        try
        {
            var transfer = await repository.GetByIdAsync(transferId, cancellationToken);
            if (transfer?.Status != TransferStatus.Scanning)
                return;

            transfer.DeferScan(DateTimeOffset.UtcNow);
            await repository.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("Scan worker retry scheduled for transfer {TransferId} at {RetryAt}.", transferId, transfer.NextScanAttemptAt);
        }
        catch (Exception retryException) when (retryException is not OperationCanceledException)
        {
            _logger.LogError(retryException, "Could not schedule scan retry for transfer {TransferId}.", transferId);
        }
    }
}
