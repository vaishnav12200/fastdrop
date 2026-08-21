using FastDrop.Application.Common.Interfaces;
using System.Security.Cryptography;

namespace FastDrop.Infrastructure.Storage;

public class LocalFileStorage : IFileStorage
{
    private const int StreamBufferSize = 1024 * 1024;
    private readonly string _baseStoragePath;

    public LocalFileStorage(string baseStoragePath = "storage/transfers")
    {
        _baseStoragePath = Path.GetFullPath(baseStoragePath);
    }

    public async Task<string> StoreChunkAsync(Guid transferId, int chunkNumber, Stream data, CancellationToken cancellationToken = default)
    {
        var transferDir = Path.Combine(_baseStoragePath, transferId.ToString(), "chunks");
        Directory.CreateDirectory(transferDir);

        // Name chunks simply: 000000, 000001, etc.
        var chunkPath = Path.Combine(transferDir, chunkNumber.ToString("D6"));

        // A 1 MiB async buffer reduces syscalls for large chunks without retaining
        // the entire upload in memory. FileShare.None prevents concurrent writers.
        await using var fileStream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        
        // We wrap the fileStream in a CryptoStream so we calculate the hash WHILE writing to disk
        await using (var cryptoStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write, leaveOpen: true))
        {
            await data.CopyToAsync(cryptoStream, cancellationToken);
            await cryptoStream.FlushFinalBlockAsync(cancellationToken);
        }
        
        return Convert.ToHexStringLower(sha256.Hash!);
    }

    public Task<Stream> OpenFinalFileAsync(Guid transferId, int totalChunks, CancellationToken cancellationToken = default)
    {
        var chunksDir = Path.Combine(_baseStoragePath, transferId.ToString(), "chunks");
        
        var chunkPaths = new List<string>();
        for (int i = 0; i < totalChunks; i++)
        {
            var chunkPath = Path.Combine(chunksDir, i.ToString("D6"));
            if (!File.Exists(chunkPath))
                throw new FileNotFoundException($"Missing chunk {i} for transfer {transferId}");
            chunkPaths.Add(chunkPath);
        }

        // Return a stream that seamlessly reads across all chunk files sequentially
        Stream compositeStream = new CompositeStream(chunkPaths);
        return Task.FromResult(compositeStream);
    }

    public Task<Stream> OpenChunkAsync(Guid transferId, int chunkNumber, CancellationToken cancellationToken = default)
    {
        var chunkPath = Path.Combine(_baseStoragePath, transferId.ToString(), "chunks", chunkNumber.ToString("D6"));
        
        if (!File.Exists(chunkPath))
            throw new FileNotFoundException($"Chunk {chunkNumber} not found.");

        Stream stream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
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
