using FastDrop.Application.Common.Interfaces;

namespace FastDrop.Infrastructure.Storage;

public class LocalFileStorage : IFileStorage
{
    private readonly string _baseStoragePath;

    public LocalFileStorage(string baseStoragePath = "storage/transfers")
    {
        _baseStoragePath = Path.GetFullPath(baseStoragePath);
    }

    public async Task StoreChunkAsync(Guid transferId, int chunkNumber, Stream data, CancellationToken cancellationToken = default)
    {
        var transferDir = Path.Combine(_baseStoragePath, transferId.ToString(), "chunks");
        Directory.CreateDirectory(transferDir);

        // Name chunks simply: 000000, 000001, etc.
        var chunkPath = Path.Combine(transferDir, chunkNumber.ToString("D6"));

        // FileShare.None prevents other processes from corrupting our write.
        // bufferSize: 81920 (80KB) is the standard optimal size before hitting the Large Object Heap in .NET
        using var fileStream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
        
        await data.CopyToAsync(fileStream, cancellationToken);
    }

    public Task<Stream> OpenChunkAsync(Guid transferId, int chunkNumber, CancellationToken cancellationToken = default)
    {
        var chunkPath = Path.Combine(_baseStoragePath, transferId.ToString(), "chunks", chunkNumber.ToString("D6"));
        
        if (!File.Exists(chunkPath))
            throw new FileNotFoundException($"Chunk {chunkNumber} not found.");

        Stream stream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        var transferDir = Path.Combine(_baseStoragePath, transferId.ToString());
        if (Directory.Exists(transferDir))
        {
            Directory.Delete(transferDir, recursive: true);
        }
        return Task.CompletedTask;
    }
}
