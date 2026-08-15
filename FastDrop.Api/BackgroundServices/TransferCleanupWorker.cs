using FastDrop.Application.Common.Interfaces;
using FastDrop.Infrastructure.Data;
using FastDrop.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FastDrop.Api.BackgroundServices;

// BackgroundService is the standard .NET base class for long-running hosted services.
// It runs alongside the web server and survives the entire application lifetime.
public class TransferCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorage _fileStorage;
    private readonly ILogger<TransferCleanupWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(15);
    private const int BatchSize = 50;

    public TransferCleanupWorker(IServiceScopeFactory scopeFactory, IFileStorage fileStorage, ILogger<TransferCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TransferCleanupWorker started. Running every {Interval}.", _interval);

        // PeriodicTimer is the modern .NET 6+ alternative to `await Task.Delay` in a loop.
        // It fires at the interval regardless of how long the previous run took,
        // and it integrates cleanly with CancellationToken so it stops immediately on shutdown.
        using var timer = new PeriodicTimer(_interval);

        // Run once immediately at startup to clean up any sessions that expired
        // while the server was offline.
        await RunCleanupAsync(stoppingToken);

        // Then keep running on the schedule until the server shuts down.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCleanupAsync(stoppingToken);
        }
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        _logger.LogInformation("Running expired transfer cleanup...");
        int totalExpired = 0;

        // We MUST create a new DI scope here because ITransferRepository is Scoped
        // (it wraps a DbContext), while this BackgroundService is a Singleton.
        // Injecting a Scoped service directly into a Singleton causes a "captive dependency"
        // bug where the DbContext is shared across requests and becomes corrupted.
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITransferRepository>();

        try
        {
            var now = DateTimeOffset.UtcNow;
            var expired = await repository.GetExpiredTransfersAsync(now, BatchSize, ct);

            foreach (var transfer in expired)
            {
                try
                {
                    _logger.LogInformation("Expiring transfer {TransferId} (expired at {ExpiresAt})", transfer.Id, transfer.ExpiresAt);

                    // 1. Delete files from disk first. If this fails, we leave
                    //    the DB record alone so we can retry on the next cycle.
                    await _fileStorage.DeleteTransferAsync(transfer.Id, ct);

                    // 2. Update the state machine in the domain entity.
                    transfer.Expire();

                    totalExpired++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete files for transfer {TransferId}. Skipping.", transfer.Id);
                }
            }

            // 3. Save all successful state changes in one batch.
            if (totalExpired > 0)
            {
                await repository.SaveChangesAsync(ct);
                _logger.LogInformation("Cleanup complete. Expired {Count} transfer(s).", totalExpired);
            }
            else
            {
                _logger.LogInformation("Cleanup complete. No expired transfers found.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let an unhandled exception kill the background worker.
            // Log it and wait for the next cycle.
            _logger.LogError(ex, "An error occurred during transfer cleanup.");
        }
    }
}
