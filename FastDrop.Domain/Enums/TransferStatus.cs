namespace FastDrop.Domain.Enums;

public enum TransferStatus
{
    Created = 0,
    WaitingForReceiver = 1,
    ReceiverConnected = 2,
    Uploading = 3,
    Ready = 4,
    Downloading = 5,
    Completed = 6,
    Cancelled = 7,
    Expired = 8,
    Failed = 9,
    Scanning = 10,
    Clean = 11,
    Blocked = 12
}
