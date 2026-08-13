using FastDrop.Application.Common.Interfaces;
using FastDrop.Domain.Entities;
using FastDrop.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FastDrop.Infrastructure.Repositories;

public class TransferRepository : ITransferRepository
{
    private readonly FastDropDbContext _dbContext;

    public TransferRepository(FastDropDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void Add(TransferSession transfer)
    {
        _dbContext.TransferSessions.Add(transfer);
    }

    public void AddChunk(ChunkMetadata chunk)
    {
        // Explicitly attach to the DbContext - do NOT rely on EF Core graph traversal
        // through private readonly fields to discover new entities.
        _dbContext.Chunks.Add(chunk);
    }

    public async Task<TransferSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _dbContext.TransferSessions
            .Include(t => t.File)
                .ThenInclude(f => f.Chunks)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _dbContext.SaveChangesAsync(ct);
    }
}
