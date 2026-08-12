using FastDrop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FastDrop.Infrastructure.Data.Configurations;

public class ChunkMetadataConfiguration : IEntityTypeConfiguration<ChunkMetadata>
{
    public void Configure(EntityTypeBuilder<ChunkMetadata> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Hash).IsRequired().HasMaxLength(256);

        // Prevent uploading the exact same chunk number for the same file twice
        // This enforces data integrity directly at the database level!
        builder.HasIndex(c => new { c.FileMetadataId, c.ChunkNumber }).IsUnique();
    }
}
