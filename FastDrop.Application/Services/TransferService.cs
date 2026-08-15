using FastDrop.Application.Common.Interfaces;
using FastDrop.Application.DTOs;
using FastDrop.Application.Security;
using FastDrop.Domain.Entities;
using FastDrop.Domain.Enums;

namespace FastDrop.Application.Services;

public class TransferService : ITransferService
{
    private readonly ITransferRepository _repository;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly IFileStorage _fileStorage;

    public TransferService(ITransferRepository repository, ITokenGenerator tokenGenerator, IFileStorage fileStorage)
    {
        _repository = repository;
        _tokenGenerator = tokenGenerator;
        _fileStorage = fileStorage;
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
        var transfer = await _repository.GetByIdAsync(transferId, cancellationToken);

        if (transfer == null) return null;

        return new TransferDetailsResponse(
            transfer.Id,
            transfer.Status.ToString(),
            transfer.File.OriginalFileName,
            transfer.File.Size,
            transfer.ExpiresAt
        );
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

        // Now ask the database for the authoritative count AFTER saving this chunk
        int receivedChunks = await _repository.GetReceivedChunkCountAsync(transfer.File.Id, cancellationToken);
        int totalChunks = transfer.File.TotalChunks;
        bool isComplete = receivedChunks >= totalChunks;

        // If all chunks are in, automatically advance the state machine to Ready.
        // "Ready" means: all bytes are on disk, the receiver can now start downloading.
        if (isComplete && transfer.Status == TransferStatus.Uploading)
        {
            // Assemble the final file and calculate its overall hash
            string finalHash = await _fileStorage.AssembleFileAsync(transfer.Id, totalChunks, cancellationToken);
            transfer.File.SetFileHash(finalHash);

            transfer.MarkAsReady();
            await _repository.SaveChangesAsync(cancellationToken);
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
        }

        var fileStream = await _fileStorage.OpenFinalFileAsync(transferId, cancellationToken);

        return new DownloadTransferResponse(
            fileStream,
            transfer.File.OriginalFileName,
            transfer.File.ContentType,
            transfer.File.Size
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
        }
        catch (InvalidOperationException) { /* Already completed or wrong state, safe to ignore. */ }
    }
}
