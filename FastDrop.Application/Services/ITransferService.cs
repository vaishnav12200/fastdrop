using FastDrop.Application.DTOs;

namespace FastDrop.Application.Services;

public interface ITransferService
{
    Task<CreateTransferResponse> CreateTransferAsync(CreateTransferRequest request, CancellationToken cancellationToken = default);
    Task<TransferDetailsResponse?> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task<bool> JoinTransferAsync(Guid transferId, JoinTransferRequest request, CancellationToken cancellationToken = default);
    Task<UploadChunkResponse?> UploadChunkAsync(Guid transferId, string senderToken, int chunkNumber, Stream data, CancellationToken cancellationToken = default);
    // Validates the receiver token, transitions state to Downloading, and returns the stream + metadata.
    Task<DownloadTransferResponse?> InitiateDownloadAsync(Guid transferId, string receiverToken, CancellationToken cancellationToken = default);
    // Called after the file stream has been fully sent to the client. Marks the transfer as Completed.
    Task CompleteDownloadAsync(Guid transferId, CancellationToken cancellationToken = default);
}
