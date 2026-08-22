namespace FastDrop.Application.Common.Interfaces;

public enum FileScanVerdict { Pending, Clean, ThreatFound, Unavailable, Rejected }

public record FileScanRequest(string FileName, long FileSize);
public record FileScanResult(FileScanVerdict Verdict, string? Detail = null, string? ScanReference = null);

/// <summary>Scans a completed, quarantined file before it can be shared.</summary>
public interface IFileSecurityScanner
{
    /// <summary>Starts scanning a bounded-memory content stream.</summary>
    Task<FileScanResult> SubmitAsync(FileScanRequest request, Stream content, CancellationToken cancellationToken = default);
    /// <summary>Gets a previously submitted asynchronous scan result.</summary>
    Task<FileScanResult> GetResultAsync(string scanReference, CancellationToken cancellationToken = default);
}
