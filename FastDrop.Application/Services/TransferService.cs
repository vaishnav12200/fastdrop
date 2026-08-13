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

    public TransferService(ITransferRepository repository, ITokenGenerator tokenGenerator)
    {
        _repository = repository;
        _tokenGenerator = tokenGenerator;
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
}
