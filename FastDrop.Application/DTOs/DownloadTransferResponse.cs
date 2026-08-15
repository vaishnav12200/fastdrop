namespace FastDrop.Application.DTOs;

// Returned by the service to give the controller everything it needs
// to write the correct HTTP headers BEFORE it starts streaming the body.
public record DownloadTransferResponse(
    Stream FileStream,
    string FileName,
    string ContentType,
    long Size
);
