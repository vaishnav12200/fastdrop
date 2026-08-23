using FastDrop.Domain.Entities;
using FastDrop.Domain.Enums;

namespace FastDrop.Tests.Domain;

public class TransferScanFlowTests
{
    [Fact]
    public void Receiver_token_can_only_be_published_after_a_clean_scan()
    {
        var file = new FileMetadata("example.txt", 5, "text/plain", 1, 5);
        var transfer = new TransferSession("sender-hash", "placeholder-hash", file, TimeSpan.FromHours(1));

        transfer.StartUpload();
        transfer.BeginScanning();

        Assert.Throws<InvalidOperationException>(() => transfer.PublishReceiver("receiver-hash"));

        transfer.MarkClean();
        transfer.PublishReceiver("receiver-hash");

        Assert.Equal(TransferStatus.WaitingForReceiver, transfer.Status);
        transfer.ConnectReceiver();
        transfer.MarkAsReady();
        Assert.Equal(TransferStatus.Ready, transfer.Status);
    }

    [Fact]
    public void Missing_quarantined_chunks_end_the_transfer_without_making_it_shareable()
    {
        var file = new FileMetadata("missing.bin", 5, "application/octet-stream", 1, 5);
        var transfer = new TransferSession("sender-hash", "placeholder-hash", file, TimeSpan.FromHours(1));

        transfer.StartUpload();
        transfer.BeginScanning();
        transfer.MarkFailed();

        Assert.Equal(TransferStatus.Failed, transfer.Status);
        Assert.Throws<InvalidOperationException>(() => transfer.PublishReceiver("receiver-hash"));
    }
}
