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

    public async Task<bool> UploadChunkAsync(Guid transferId, string senderToken, int chunkNumber, Stream data, CancellationToken cancellationToken = default)
    {
        var transfer = await _repository.GetByIdAsync(transferId, cancellationToken);
        if (transfer == null) return false;

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

        if (transfer.File.Chunks.Any(c => c.ChunkNumber == chunkNumber))
        {
            return true; // Idempotency: chunk already exists, safely ignore duplicate network retry
        }

        // Stream the bytes directly to disk!
        await _fileStorage.StoreChunkAsync(transferId, chunkNumber, data, cancellationToken);

        // Explicitly add the chunk to the DbContext via the repository.
        // Do NOT use transfer.File.AddChunk() here — EF Core cannot reliably detect
        // new entities added to a private readonly backing field via graph traversal.
        var chunkMeta = new ChunkMetadata(transfer.File.Id, chunkNumber, 0, "pending_hash");
        _repository.AddChunk(chunkMeta);

        await _repository.SaveChangesAsync(cancellationToken);
        return true;
    }
}
