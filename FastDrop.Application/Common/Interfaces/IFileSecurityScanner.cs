namespace FastDrop.Application.Common.Interfaces;

public enum FileScanVerdict { Clean, ThreatFound, Unavailable }

public record FileScanResult(FileScanVerdict Verdict, string? Detail = null);

/// <summary>Scans a completed, quarantined file before it can be shared.</summary>
public interface IFileSecurityScanner
{
    Task<FileScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default);
}
