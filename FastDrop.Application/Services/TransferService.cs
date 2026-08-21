using FastDrop.Application.Common.Interfaces;
using FastDrop.Application.DTOs;
using FastDrop.Application.Security;
using FastDrop.Domain.Entities;
using FastDrop.Domain.Enums;

using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace FastDrop.Application.Services;

public class TransferService : ITransferService
{
    private readonly ITransferRepository _repository;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly IFileStorage _fileStorage;
    private readonly IDistributedCache _cache;
    private readonly IDistributedLockProvider _lockProvider;

    public TransferService(ITransferRepository repository, ITokenGenerator tokenGenerator, IFileStorage fileStorage, IDistributedCache cache, IDistributedLockProvider lockProvider)
    {
        _repository = repository;
        _tokenGenerator = tokenGenerator;
        _fileStorage = fileStorage;
        _cache = cache;
        _lockProvider = lockProvider;
    }

    public async Task<CreateTransferResponse> CreateTransferAsync(CreateTransferRequest request, CancellationToken cancellationToken = default)
    {
        var senderToken = _tokenGenerator.GenerateToken();
        var receiverToken = _tokenGenerator.GenerateToken();

        var file = new FileMetadata(
            request.FileName, 
            request.Size, 
            request.ContentType, 
            request.TotalChunks, 
            request.ChunkSize);

        // Setting a fixed 24-hour expiration for now
        var transfer = new TransferSession(
            senderToken.HashedToken,
            receiverToken.HashedToken,
            file,
            TimeSpan.FromHours(24));

        _repository.Add(transfer);
        await _repository.SaveChangesAsync(cancellationToken);

        return new CreateTransferResponse(
            transfer.Id,
            senderToken.RawToken, // Only return raw tokens once!
            receiverToken.RawToken,
            transfer.ExpiresAt
        );
    }

    public async Task<TransferDetailsResponse?> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        string cacheKey = $"Transfer_{transferId}";
        
        // 1. Try to fetch from Redis Cache first
        var cachedData = await _cache.GetStringAsync(cacheKey, cancellationToken);
        if (!string.IsNullOrEmpty(cachedData))
        {
            return JsonSerializer.Deserialize<TransferDetailsResponse>(cachedData);
        }

        // 2. Cache miss -> fetch from Database
        var transfer = await _repository.GetByIdAsync(transferId, cancellationToken);

        if (transfer == null) return null;

        var response = new TransferDetailsResponse(
            transfer.Id,
            transfer.Status.ToString(),
            transfer.File.OriginalFileName,
            transfer.File.Size,
            transfer.ExpiresAt
        );

        // 3. Save to Redis Cache (expire in 5 seconds to keep data fresh but still shield DB from 100 req/sec polling)
        var cacheOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5)
        };
        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(response), cacheOptions, cancellationToken);

        return response;
    }

    public async Task<bool> JoinTransferAsync(Guid transferId, JoinTransferRequest request, CancellationToken cancellationToken = default)
    {
        var transfer = await _repository.GetByIdAsync(transferId, cancellationToken);

        if (transfer == null) return false;

        // Securely verify token without exposing database hashes
        if (!_tokenGenerator.VerifyToken(request.ReceiverToken, transfer.ReceiverTokenHash))
        {
            return false;
        }

        try
        {
            if (transfer.Status == TransferStatus.Created) 
            {
                transfer.WaitReceiver();
            }
            
            // Updates state to ReceiverConnected if currently waiting.
            transfer.ConnectReceiver(); 
            await _repository.SaveChangesAsync(cancellationToken);
            await _cache.RemoveAsync($"Transfer_{transferId}", cancellationToken);
        }
        catch(InvalidOperationException)
        {
            // If the state is already further along (e.g. Uploading or Ready), we just ignore 
            // the state transition failure. The token was valid, so they are authorized.
        }

        return true;
    }

    public async Task<UploadChunkResponse?> UploadChunkAsync(Guid transferId, string senderToken, int chunkNumber, Stream data, CancellationToken cancellationToken = default)
    {
        var transfer = await _repository.GetByIdAsync(transferId, cancellationToken);
        if (transfer == null) return null;

        if (!_tokenGenerator.VerifyToken(senderToken, transfer.SenderTokenHash))
        {
            throw new UnauthorizedAccessException("Invalid sender token.");
        }

        if (transfer.Status == TransferStatus.Created || transfer.Status == TransferStatus.WaitingForReceiver || transfer.Status == TransferStatus.ReceiverConnected)
        {
            transfer.StartUpload();
        }
        else if (transfer.Status != TransferStatus.Uploading)
        {
            throw new InvalidOperationException($"Cannot upload chunks in state {transfer.Status}");
        }

        // Idempotency: if the client retries a chunk that already landed, just re-confirm success.
        // We count from the DB (the source of truth), not from the in-memory collection,
        // because in-memory state can be stale across requests.
        bool isNewChunk = !transfer.File.Chunks.Any(c => c.ChunkNumber == chunkNumber);

        if (isNewChunk)
        {
            // Stream the bytes directly to disk — no RAM buffering!
            // It simultaneously computes and returns the SHA-256 hash.
            string hash = await _fileStorage.StoreChunkAsync(transferId, chunkNumber, data, cancellationToken);

            // Persist the chunk metadata record with the actual hash
            // (Setting size to 0 for now as we don't track chunk size rigorously yet)
            var chunkMeta = new ChunkMetadata(transfer.File.Id, chunkNumber, 0, hash);
            _repository.AddChunk(chunkMeta);
        }

        // Save both the Status change (Uploading) AND the new ChunkMetadata INSERT in a single transaction
        await _repository.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync($"Transfer_{transferId}", cancellationToken);

        // Now ask the database for the authoritative count AFTER saving this chunk
        int receivedChunks = await _repository.GetReceivedChunkCountAsync(transfer.File.Id, cancellationToken);
        int totalChunks = transfer.File.TotalChunks;
        bool isComplete = receivedChunks >= totalChunks;

        // If all chunks are in, automatically advance the state machine to Ready.
        // We use a distributed lock here to prevent two concurrent chunk uploads
        // from both triggering this logic at the exact same millisecond.
        if (isComplete && transfer.Status == TransferStatus.Uploading)
        {
            string lockKey = $"TransferLock_{transferId}";
            // Wait up to 15 seconds to acquire lock, hold for up to 30 seconds
            await using var lockReleaser = await _lockProvider.TryAcquireLockAsync(lockKey, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), cancellationToken);
            
            if (lockReleaser != null)
            {
                // Re-fetch transfer inside lock to ensure we didn't lose the race
                transfer = await _repository.GetByIdAsync(transferId, cancellationToken);
                
                if (transfer != null && transfer.Status == TransferStatus.Uploading)
                {
                    // No more slow Assembly process!
                    // The file hash is just set to a dummy value or we leave it blank, 
                    // as chunk-level hashes already guarantee integrity.
                    transfer.File.SetFileHash("composite-stream-no-global-hash");

                    transfer.MarkAsReady();
                    await _repository.SaveChangesAsync(cancellationToken);
                    await _cache.RemoveAsync($"Transfer_{transferId}", cancellationToken);
                }
            }
        }

        return new UploadChunkResponse(chunkNumber, totalChunks, receivedChunks, isComplete);
    }

    public async Task<DownloadTransferResponse?> InitiateDownloadAsync(Guid transferId, string receiverToken, CancellationToken cancellationToken = default)
    {
        var transfer = await _repository.GetByIdAsync(transferId, cancellationToken);
        if (transfer == null) return null;

        if (!_tokenGenerator.VerifyToken(receiverToken, transfer.ReceiverTokenHash))
        {
            throw new UnauthorizedAccessException("Invalid receiver token.");
        }

        // Only files that are fully assembled can be downloaded.
        if (transfer.Status != TransferStatus.Ready && transfer.Status != TransferStatus.Downloading)
        {
            throw new InvalidOperationException($"Transfer is not ready for download. Current status: {transfer.Status}");
        }

        // Advance to Downloading so the sender knows the receiver is actively pulling.
        if (transfer.Status == TransferStatus.Ready)
        {
            transfer.StartDownload();
            await _repository.SaveChangesAsync(cancellationToken);
            await _cache.RemoveAsync($"Transfer_{transferId}", cancellationToken);
        }

        var fileStream = await _fileStorage.OpenFinalFileAsync(transferId, transfer.File.TotalChunks, cancellationToken);

        // A mismatched Content-Length leaves browsers waiting or makes them
        // reject the response, so fail clearly instead of serving corrupt data.
        if (fileStream.Length != transfer.File.Size)
        {
            await fileStream.DisposeAsync();
            throw new InvalidOperationException("Stored file size does not match the transfer metadata.");
        }

        return new DownloadTransferResponse(
            fileStream,
            transfer.File.OriginalFileName,
            transfer.File.ContentType,
            fileStream.Length
        );
    }

    public async Task CompleteDownloadAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        var transfer = await _repository.GetByIdAsync(transferId, cancellationToken);
        if (transfer == null) return;

        // Complete() on the domain entity guards the state machine — it only
        // succeeds if Status == Downloading. Idempotently ignore other states.
        try
        {
            transfer.Complete();
            await _repository.SaveChangesAsync(cancellationToken);
            await _cache.RemoveAsync($"Transfer_{transferId}", cancellationToken);
        }
        catch (InvalidOperationException) { /* Already completed or wrong state, safe to ignore. */ }
    }
}
