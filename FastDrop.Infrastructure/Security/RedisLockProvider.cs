using FastDrop.Application.Common.Interfaces;
using StackExchange.Redis;
using System.Diagnostics;

namespace FastDrop.Infrastructure.Security;

public class RedisLockProvider : IDistributedLockProvider
{
    private readonly IConnectionMultiplexer _redis;

    public RedisLockProvider(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<IAsyncDisposable?> TryAcquireLockAsync(string key, TimeSpan expiry, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        string lockValue = Guid.NewGuid().ToString(); // Unique value to ensure we only release our own lock
        
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed <= timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool acquired = await db.LockTakeAsync(key, lockValue, expiry);
            if (acquired)
            {
                return new RedisLockReleaser(db, key, lockValue);
            }

            // Wait a short bit before retrying to prevent hammering Redis
            await Task.Delay(50, cancellationToken);
        }

        return null; // Timed out
    }

    private class RedisLockReleaser : IAsyncDisposable
    {
        private readonly IDatabase _db;
        private readonly string _key;
        private readonly string _value;
        private bool _disposed;

        public RedisLockReleaser(IDatabase db, string key, string value)
        {
            _db = db;
            _key = key;
            _value = value;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                await _db.LockReleaseAsync(_key, _value);
                _disposed = true;
            }
        }
    }
}
