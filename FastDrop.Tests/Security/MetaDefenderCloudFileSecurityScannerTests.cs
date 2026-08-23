using System.Net;
using System.Text;
using FastDrop.Application.Common.Interfaces;
using FastDrop.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FastDrop.Tests.Security;

public class MetaDefenderCloudFileSecurityScannerTests
{
    [Fact]
    public async Task GetResultAsync_returns_clean_only_for_a_completed_zero_detection_report()
    {
        var scanner = CreateScanner("{\"process_info\":{\"progress_percentage\":100},\"scan_results\":{\"scan_all_result_i\":0,\"total_avs\":23,\"total_detected_avs\":0}}");

        var result = await scanner.GetResultAsync("scan-123");

        Assert.Equal(FileScanVerdict.Clean, result.Verdict);
    }

    [Fact]
    public async Task GetResultAsync_blocks_a_detected_file()
    {
        var scanner = CreateScanner("{\"process_info\":{\"progress_percentage\":100},\"scan_results\":{\"scan_all_result_i\":1,\"total_avs\":23,\"total_detected_avs\":1}}");

        var result = await scanner.GetResultAsync("scan-123");

        Assert.Equal(FileScanVerdict.ThreatFound, result.Verdict);
    }

    [Fact]
    public async Task SubmitAsync_rejects_files_above_the_configured_contract_limit_without_uploading()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("The request must not be sent."));
        var scanner = CreateScanner("{}", handler, maximumFileSizeBytes: 10);

        var result = await scanner.SubmitAsync(new FileScanRequest("large.bin", 11), new MemoryStream(new byte[11]));

        Assert.Equal(FileScanVerdict.Rejected, result.Verdict);
    }

    [Fact]
    public async Task SubmitAsync_keeps_files_quarantined_when_the_api_key_is_missing()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("The request must not be sent."));
        var scanner = CreateScanner("{}", handler, apiKey: null);

        var result = await scanner.SubmitAsync(new FileScanRequest("example.txt", 5), new MemoryStream("hello"u8.ToArray()));

        Assert.Equal(FileScanVerdict.Unavailable, result.Verdict);
    }

    private static MetaDefenderCloudFileSecurityScanner CreateScanner(string responseBody, HttpMessageHandler? handler = null,
        long maximumFileSizeBytes = 1_073_741_824, string? apiKey = "test-key")
    {
        var values = new Dictionary<string, string?>
        {
            ["MalwareScanner:MetaDefenderCloud:ApiKey"] = apiKey,
            ["MalwareScanner:MetaDefenderCloud:MaximumFileSizeBytes"] = maximumFileSizeBytes.ToString(),
            ["MalwareScanner:MetaDefenderCloud:PrivateScanning"] = "true"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var client = new HttpClient(handler ?? new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        }))
        {
            BaseAddress = new Uri("https://scanner.example/v4/")
        };
        return new MetaDefenderCloudFileSecurityScanner(client, configuration, NullLogger<MetaDefenderCloudFileSecurityScanner>.Instance);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
