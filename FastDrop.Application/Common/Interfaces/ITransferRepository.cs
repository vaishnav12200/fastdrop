using FastDrop.Domain.Entities;

namespace FastDrop.Application.Common.Interfaces;

public interface ITransferRepository
{
    void Add(TransferSession transfer);
    Task<TransferSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
