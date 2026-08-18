namespace FastDrop.Application.Common.Interfaces;

public interface IDistributedLockProvider
{
    /// <summary>
    /// Attempts to acquire a distributed lock.
    /// Returns an IAsyncDisposable that releases the lock upon disposal.
    /// Returns null if the lock could not be acquired.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireLockAsync(string key, TimeSpan expiry, TimeSpan timeout, CancellationToken cancellationToken = default);
}
