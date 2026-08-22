using FastDrop.Application.Common.Interfaces;
using FastDrop.Domain.Entities;
using FastDrop.Domain.Enums;
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

    public async Task<int> GetReceivedChunkCountAsync(Guid fileMetadataId, CancellationToken ct = default)
    {
        // CountAsync translates to a fast SQL COUNT(*) - never load the rows into memory just to count them
        return await _dbContext.Chunks
            .CountAsync(c => c.FileMetadataId == fileMetadataId, ct);
    }

    public async Task<List<TransferSession>> GetByStatusAsync(TransferStatus status, int batchSize, CancellationToken ct = default)
    {
        return await _dbContext.TransferSessions
            .Include(t => t.File)
                .ThenInclude(f => f.Chunks)
            .Where(t => t.Status == status)
            .OrderBy(t => t.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task<List<TransferSession>> GetExpiredTransfersAsync(DateTimeOffset now, int batchSize, CancellationToken ct = default)
    {
        // Terminal states — no action needed, never include them.
        var terminalStates = new[] { TransferStatus.Completed, TransferStatus.Expired, TransferStatus.Cancelled };

        return await _dbContext.TransferSessions
            .Where(t => t.ExpiresAt <= now && !terminalStates.Contains(t.Status))
            .OrderBy(t => t.ExpiresAt) // Oldest first
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _dbContext.SaveChangesAsync(ct);
    }
}
