namespace FastDrop.Application.Common.Interfaces;

public interface IFileStorage
{
    // Streams data to disk and simultaneously computes its SHA-256 hash.
    // Returns the hex-encoded hash of the written bytes.
    Task<string> StoreChunkAsync(Guid transferId, int chunkNumber, Stream data, CancellationToken cancellationToken = default);
    Task<Stream> OpenChunkAsync(Guid transferId, int chunkNumber, CancellationToken cancellationToken = default);
    Task DeleteTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
}
