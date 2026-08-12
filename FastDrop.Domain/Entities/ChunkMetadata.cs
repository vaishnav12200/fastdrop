namespace FastDrop.Domain.Entities;

public class ChunkMetadata
{
    public Guid Id { get; private set; }
    public Guid FileMetadataId { get; private set; }
    public int ChunkNumber { get; private set; }
    public int Size { get; private set; }
    public string Hash { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }

    private ChunkMetadata()
    {
        // Required for EF Core
        Hash = null!;
    }

    public ChunkMetadata(Guid fileMetadataId, int chunkNumber, int size, string hash)
    {
        Id = Guid.NewGuid();
        FileMetadataId = fileMetadataId;
        ChunkNumber = chunkNumber;
        Size = size;
        Hash = hash;
        UploadedAt = DateTimeOffset.UtcNow;
    }
}
