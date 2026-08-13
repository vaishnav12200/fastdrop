namespace FastDrop.Application.Common.Interfaces;

public interface IFileStorage
{
    // Note how we accept a raw Stream. This allows us to pipe data directly from the network socket to disk!
    Task StoreChunkAsync(Guid transferId, int chunkNumber, Stream data, CancellationToken cancellationToken = default);
    Task<Stream> OpenChunkAsync(Guid transferId, int chunkNumber, CancellationToken cancellationToken = default);
    Task DeleteTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
}
