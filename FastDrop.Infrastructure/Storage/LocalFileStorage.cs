using FastDrop.Application.Common.Interfaces;
using System.Security.Cryptography;

namespace FastDrop.Infrastructure.Storage;

public class LocalFileStorage : IFileStorage
{
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

        // FileShare.None prevents other processes from corrupting our write.
        // bufferSize: 81920 (80KB) is the standard optimal size before hitting the Large Object Heap in .NET
        using var fileStream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
        using var sha256 = SHA256.Create();
        
        // We wrap the fileStream in a CryptoStream so we calculate the hash WHILE writing to disk
        await using (var cryptoStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write, leaveOpen: true))
        {
            await data.CopyToAsync(cryptoStream, cancellationToken);
            await cryptoStream.FlushFinalBlockAsync(cancellationToken);
        }
        
        return Convert.ToHexStringLower(sha256.Hash!);
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
