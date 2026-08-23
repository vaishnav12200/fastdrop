using FastDrop.Domain.Enums;

namespace FastDrop.Domain.Entities;

public class TransferSession
{
    public Guid Id { get; private set; }
    
    // We only store cryptographic hashes in the database, never raw tokens!
    public string SenderTokenHash { get; private set; }
    public string ReceiverTokenHash { get; private set; }
    
    public TransferStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? ScannerReference { get; private set; }
    public DateTimeOffset? NextScanAttemptAt { get; private set; }
    public int ScanAttemptCount { get; private set; }

    public FileMetadata File { get; private set; }

    private TransferSession()
    {
        // Required for EF Core. The null! operator tells the compiler to ignore the uninitialized non-nullable properties, as EF Core will populate them via reflection.
        SenderTokenHash = null!;
        ReceiverTokenHash = null!;
        File = null!;
    }

    public TransferSession(string senderTokenHash, string receiverTokenHash, FileMetadata file, TimeSpan timeToLive)
    {
        Id = Guid.NewGuid();
        SenderTokenHash = senderTokenHash;
        ReceiverTokenHash = receiverTokenHash;
        File = file;
        Status = TransferStatus.Created;
        CreatedAt = DateTimeOffset.UtcNow;
        ExpiresAt = CreatedAt.Add(timeToLive);
    }

    // --- State Machine Methods ---

    public void WaitReceiver()
    {
        EnsureState(TransferStatus.Created);
        Status = TransferStatus.WaitingForReceiver;
    }

    public void ConnectReceiver()
    {
        // A receiver can connect while we are waiting, or even while uploading
        if (Status != TransferStatus.WaitingForReceiver && Status != TransferStatus.Uploading)
            throw new InvalidOperationException($"Cannot transition from {Status} to {TransferStatus.ReceiverConnected}");
            
        // If it's already uploading, receiver just connected but we don't change status away from Uploading
        if (Status == TransferStatus.WaitingForReceiver)
        {
            Status = TransferStatus.ReceiverConnected;
        }
    }

    public void StartUpload()
    {
        if (Status != TransferStatus.Created && Status != TransferStatus.Uploading)
            throw new InvalidOperationException($"Cannot transition from {Status} to {TransferStatus.Uploading}");
            
        Status = TransferStatus.Uploading;
    }

    public void MarkAsReady()
    {
        if (Status != TransferStatus.Uploading && Status != TransferStatus.ReceiverConnected)
            throw new InvalidOperationException($"Cannot transition from {Status} to {TransferStatus.Ready}");
        Status = TransferStatus.Ready;
    }

    public void BeginScanning()
    {
        EnsureState(TransferStatus.Uploading);
        Status = TransferStatus.Scanning;
        ScannerReference = null;
        NextScanAttemptAt = null;
        ScanAttemptCount = 0;
    }

    public void RecordPendingScan(string scanReference, DateTimeOffset now)
    {
        EnsureState(TransferStatus.Scanning);
        ScannerReference = scanReference;
        NextScanAttemptAt = now.AddSeconds(5);
        ScanAttemptCount = 0;
    }

    public void DeferScan(DateTimeOffset now)
    {
        EnsureState(TransferStatus.Scanning);
        ScanAttemptCount++;
        var delaySeconds = Math.Min(300, 5 * (1 << Math.Min(ScanAttemptCount - 1, 6)));
        NextScanAttemptAt = now.AddSeconds(delaySeconds);
    }

    public void MarkClean()
    {
        EnsureState(TransferStatus.Scanning);
        Status = TransferStatus.Clean;
        NextScanAttemptAt = null;
    }

    /// <summary>
    /// Ends a scan when the quarantined content can no longer be recovered,
    /// such as after a host restart removed local chunks. A failed transfer is
    /// never shareable and must not be submitted to a scanner again.
    /// </summary>
    public void MarkFailed()
    {
        EnsureState(TransferStatus.Scanning);
        Status = TransferStatus.Failed;
        ScannerReference = null;
        NextScanAttemptAt = null;
    }

    public void Block()
    {
        EnsureState(TransferStatus.Scanning);
        Status = TransferStatus.Blocked;
        NextScanAttemptAt = null;
    }

    public void PublishReceiver(string receiverTokenHash)
    {
        EnsureState(TransferStatus.Clean);
        ReceiverTokenHash = receiverTokenHash;
        Status = TransferStatus.WaitingForReceiver;
    }

    public void StartDownload()
    {
        EnsureState(TransferStatus.Ready);
        Status = TransferStatus.Downloading;
    }

    public void Complete()
    {
        EnsureState(TransferStatus.Downloading);
        Status = TransferStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
    }
    
    public void Cancel()
    {
        if (Status == TransferStatus.Completed || Status == TransferStatus.Expired)
            throw new InvalidOperationException($"Cannot cancel a transfer in {Status} state.");
            
        Status = TransferStatus.Cancelled;
    }

    public void Expire()
    {
        if (Status == TransferStatus.Completed)
            throw new InvalidOperationException("Cannot expire a completed transfer.");
            
        Status = TransferStatus.Expired;
    }

    private void EnsureState(TransferStatus expectedStatus)
    {
        if (Status != expectedStatus)
            throw new InvalidOperationException($"Invalid state transition. Expected {expectedStatus} but was {Status}.");
    }
}
