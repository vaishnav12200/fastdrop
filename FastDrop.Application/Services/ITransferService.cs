using FastDrop.Application.DTOs;

namespace FastDrop.Application.Services;

public interface ITransferService
{
    Task<CreateTransferResponse> CreateTransferAsync(CreateTransferRequest request, CancellationToken cancellationToken = default);
    Task<TransferDetailsResponse?> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task<bool> JoinTransferAsync(Guid transferId, JoinTransferRequest request, CancellationToken cancellationToken = default);
    Task<bool> UploadChunkAsync(Guid transferId, string senderToken, int chunkNumber, Stream data, CancellationToken cancellationToken = default);
}
