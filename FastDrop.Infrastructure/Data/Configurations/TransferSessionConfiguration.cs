using FastDrop.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FastDrop.Infrastructure.Data.Configurations;

public class TransferSessionConfiguration : IEntityTypeConfiguration<TransferSession>
{
    public void Configure(EntityTypeBuilder<TransferSession> builder)
    {
        builder.HasKey(t => t.Id);

        // Required string fields
        builder.Property(t => t.SenderTokenHash).IsRequired().HasMaxLength(256);
        builder.Property(t => t.ReceiverTokenHash).IsRequired().HasMaxLength(256);

        // Status is an enum, we store it as an integer in the database by default (efficient)
        builder.Property(t => t.Status).IsRequired();

        // Index on ExpiresAt since we'll frequently query for expired transfers to clean them up
        builder.HasIndex(t => t.ExpiresAt);
        
        // Index on Status to quickly find transfers in a specific state
        builder.HasIndex(t => t.Status);
    }
}
