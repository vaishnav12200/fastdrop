namespace FastDrop.Domain.Entities;

public class FileMetadata
{
    public Guid Id { get; private set; }
    public string OriginalFileName { get; private set; }
    public long Size { get; private set; }
    public string ContentType { get; private set; }
    public int TotalChunks { get; private set; }
    public int ChunkSize { get; private set; }
    public string? FileHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // Navigation property for Entity Framework Core
    private readonly List<ChunkMetadata> _chunks = new();
    public IReadOnlyCollection<ChunkMetadata> Chunks => _chunks.AsReadOnly();

    private FileMetadata()
    {
        // Required for EF Core
        OriginalFileName = null!;
        ContentType = null!;
    }

    public FileMetadata(string originalFileName, long size, string contentType, int totalChunks, int chunkSize)
    {
        Id = Guid.NewGuid();
        OriginalFileName = originalFileName;
        Size = size;
        ContentType = contentType;
        TotalChunks = totalChunks;
        ChunkSize = chunkSize;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void SetFileHash(string fileHash)
    {
        FileHash = fileHash;
    }

    public void AddChunk(ChunkMetadata chunk)
    {
        _chunks.Add(chunk);
    }
}
