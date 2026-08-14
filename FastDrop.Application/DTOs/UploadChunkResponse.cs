namespace FastDrop.Application.DTOs;

public record UploadChunkResponse(
    int ChunkNumber,
    int TotalChunks,
    int ReceivedChunks,
    bool IsComplete   // true when all chunks have arrived
);
