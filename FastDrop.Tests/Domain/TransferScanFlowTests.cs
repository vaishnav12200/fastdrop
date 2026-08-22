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
}
