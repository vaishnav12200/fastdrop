using FastDrop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace FastDrop.Infrastructure.Data;

public class FastDropDbContext : DbContext
{
    public FastDropDbContext(DbContextOptions<FastDropDbContext> options) : base(options) { }

    public DbSet<TransferSession> TransferSessions => Set<TransferSession>();
    public DbSet<FileMetadata> Files => Set<FileMetadata>();
    public DbSet<ChunkMetadata> Chunks => Set<ChunkMetadata>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // This automatically applies all configuration classes that implement IEntityTypeConfiguration in this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}
