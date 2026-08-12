using FastDrop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FastDrop.Infrastructure.Data.Configurations;

public class FileMetadataConfiguration : IEntityTypeConfiguration<FileMetadata>
{
    public void Configure(EntityTypeBuilder<FileMetadata> builder)
    {
        builder.HasKey(f => f.Id);

        builder.Property(f => f.OriginalFileName).IsRequired().HasMaxLength(1024);
        builder.Property(f => f.ContentType).IsRequired().HasMaxLength(256);
        builder.Property(f => f.FileHash).HasMaxLength(256);

        // One-to-Many relationship: One FileMetadata has many ChunkMetadata
        builder.HasMany(f => f.Chunks)
               .WithOne() // The Chunk doesn't have a navigation property back to FileMetadata, just the foreign key
               .HasForeignKey(c => c.FileMetadataId)
               .OnDelete(DeleteBehavior.Cascade); // If we delete the file, automatically delete all its chunks from the database
    }
}
