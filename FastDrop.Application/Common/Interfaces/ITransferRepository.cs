using FastDrop.Domain.Entities;

namespace FastDrop.Application.Common.Interfaces;

public interface ITransferRepository
{
    void Add(TransferSession transfer);
    void AddChunk(ChunkMetadata chunk);
    Task<TransferSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<int> GetReceivedChunkCountAsync(Guid fileMetadataId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
