using FastDrop.Domain.Entities;
using FastDrop.Domain.Enums;

namespace FastDrop.Application.Common.Interfaces;

public interface ITransferRepository
{
    void Add(TransferSession transfer);
    void AddChunk(ChunkMetadata chunk);
    Task<TransferSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<int> GetReceivedChunkCountAsync(Guid fileMetadataId, CancellationToken ct = default);
    Task<List<TransferSession>> GetByStatusAsync(TransferStatus status, int batchSize, CancellationToken ct = default);
    // Fetch up to `batchSize` sessions that have passed their ExpiresAt and are not in a terminal state.
    Task<List<TransferSession>> GetExpiredTransfersAsync(DateTimeOffset now, int batchSize, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
