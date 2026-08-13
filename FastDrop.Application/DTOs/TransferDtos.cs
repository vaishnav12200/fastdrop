namespace FastDrop.Application.DTOs;

public record CreateTransferRequest(
    string FileName, 
    long Size, 
    string ContentType, 
    int TotalChunks, 
    int ChunkSize
);

public record CreateTransferResponse(
    Guid TransferId, 
    string SenderToken, 
    string ReceiverToken, 
    DateTimeOffset ExpiresAt
);

public record TransferDetailsResponse(
    Guid TransferId,
    string Status,
    string FileName,
    long Size,
    DateTimeOffset ExpiresAt
);

public record JoinTransferRequest(string ReceiverToken);
