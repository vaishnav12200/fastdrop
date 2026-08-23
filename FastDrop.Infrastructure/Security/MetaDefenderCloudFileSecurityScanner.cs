using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FastDrop.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FastDrop.Infrastructure.Security;

/// <summary>
/// MetaDefender Cloud adapter. It uploads a quarantined stream once, persists
/// the returned data_id through the caller, and polls that reference thereafter.
/// No full file is loaded into memory.
/// </summary>
public sealed class MetaDefenderCloudFileSecurityScanner : IFileSecurityScanner
{
    private readonly HttpClient _client;
    private readonly string? _apiKey;
    private readonly long _maximumFileSizeBytes;
    private readonly bool _privateScanning;
    private readonly TimeSpan _uploadTimeout;
    private readonly TimeSpan _pollTimeout;
    private readonly ILogger<MetaDefenderCloudFileSecurityScanner> _logger;

    public MetaDefenderCloudFileSecurityScanner(HttpClient client, IConfiguration configuration,
        ILogger<MetaDefenderCloudFileSecurityScanner> logger)
    {
        _client = client;
        _apiKey = configuration["MalwareScanner:MetaDefenderCloud:ApiKey"];
        _maximumFileSizeBytes = long.TryParse(configuration["MalwareScanner:MetaDefenderCloud:MaximumFileSizeBytes"], out var configuredMaximum)
            ? configuredMaximum : 1_073_741_824;
        _privateScanning = !bool.TryParse(configuration["MalwareScanner:MetaDefenderCloud:PrivateScanning"], out var privateScanning) || privateScanning;
        _uploadTimeout = TimeSpan.FromMinutes(int.TryParse(configuration["MalwareScanner:MetaDefenderCloud:UploadTimeoutMinutes"], out var minutes) ? minutes : 45);
        _pollTimeout = TimeSpan.FromSeconds(int.TryParse(configuration["MalwareScanner:MetaDefenderCloud:PollTimeoutSeconds"], out var seconds) ? seconds : 20);
        _logger = logger;
    }

    public async Task<FileScanResult> SubmitAsync(FileScanRequest request, Stream content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogError("MetaDefender Cloud API key is not configured; the transfer will remain quarantined.");
            return new FileScanResult(FileScanVerdict.Unavailable, "MetaDefender Cloud API key is not configured.");
        }
        if (request.FileSize < 0 || request.FileSize > _maximumFileSizeBytes)
        {
            _logger.LogWarning("File size {FileSize} exceeds the configured MetaDefender Cloud limit of {MaximumFileSizeBytes} bytes.", request.FileSize, _maximumFileSizeBytes);
            return new FileScanResult(FileScanVerdict.Rejected, $"File exceeds the configured scanner limit of {_maximumFileSizeBytes} bytes.");
        }

        try
        {
            _logger.LogInformation("Uploading {FileSize} bytes to MetaDefender Cloud for malware scanning.", request.FileSize);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_uploadTimeout);
            using var message = new HttpRequestMessage(HttpMethod.Post, "file");
            AddHeaders(message, request.FileName, includePrivateScanning: true);

            var body = new StreamContent(content, 1024 * 1024);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            body.Headers.ContentLength = request.FileSize;
            message.Content = body;

            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                return new FileScanResult(FileScanVerdict.Unavailable, $"MetaDefender Cloud returned {(int)response.StatusCode}.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new FileScanResult(FileScanVerdict.Unavailable, "MetaDefender Cloud authentication or private-scanning configuration was rejected.");
            if (!response.IsSuccessStatusCode)
                return new FileScanResult(FileScanVerdict.Rejected, $"MetaDefender Cloud rejected the upload ({(int)response.StatusCode}).");

            await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: timeout.Token);
            if (!document.RootElement.TryGetProperty("data_id", out var dataId) || string.IsNullOrWhiteSpace(dataId.GetString()))
                return new FileScanResult(FileScanVerdict.Unavailable, "MetaDefender Cloud did not return a scan reference.");

            _logger.LogInformation("MetaDefender Cloud accepted the file and returned scan reference {ScanReference}.", dataId.GetString());
            return new FileScanResult(FileScanVerdict.Pending, ScanReference: dataId.GetString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FileScanResult(FileScanVerdict.Unavailable, "MetaDefender Cloud upload timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MetaDefender Cloud upload failed.");
            return new FileScanResult(FileScanVerdict.Unavailable, "MetaDefender Cloud upload failed.");
        }
    }

    public async Task<FileScanResult> GetResultAsync(string scanReference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogError("MetaDefender Cloud API key is not configured; the transfer will remain quarantined.");
            return new FileScanResult(FileScanVerdict.Unavailable, "MetaDefender Cloud API key is not configured.");
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                _logger.LogInformation("Polling MetaDefender Cloud scan reference {ScanReference}, attempt {Attempt}.", scanReference, attempt + 1);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_pollTimeout);
                using var message = new HttpRequestMessage(HttpMethod.Get, $"file/{Uri.EscapeDataString(scanReference)}");
                AddHeaders(message);
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    if (attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken); continue; }
                    return new FileScanResult(FileScanVerdict.Unavailable, $"MetaDefender Cloud returned {(int)response.StatusCode} while polling.");
                }
                if (!response.IsSuccessStatusCode)
                    return new FileScanResult(FileScanVerdict.Unavailable, $"MetaDefender Cloud poll failed ({(int)response.StatusCode}).");

                await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: timeout.Token);
                return ParseResult(document.RootElement, scanReference);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == 2) return new FileScanResult(FileScanVerdict.Unavailable, "MetaDefender Cloud poll timed out.");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "MetaDefender Cloud poll failed.");
                if (attempt == 2) return new FileScanResult(FileScanVerdict.Unavailable, "MetaDefender Cloud poll failed.");
            }
        }

        return new FileScanResult(FileScanVerdict.Unavailable, "MetaDefender Cloud poll failed.");
    }

    private FileScanResult ParseResult(JsonElement root, string scanReference)
    {
        var progress = ReadInt(root, "process_info", "progress_percentage");
        var result = ReadInt(root, "scan_results", "scan_all_result_i");
        var detected = ReadInt(root, "scan_results", "total_detected_avs");
        var engines = ReadInt(root, "scan_results", "total_avs");

        if (progress is null || progress < 100 || result is 255)
            return new FileScanResult(FileScanVerdict.Pending, ScanReference: scanReference);
        if (result == 0 && engines > 0 && detected == 0)
            return new FileScanResult(FileScanVerdict.Clean);
        if (detected > 0)
            return new FileScanResult(FileScanVerdict.ThreatFound, $"Detected by {detected} of {engines} engines.");

        // A completed but unrecognised/non-clean response is never considered safe.
        return new FileScanResult(FileScanVerdict.Unavailable, "MetaDefender Cloud returned an unknown or incomplete verdict.");
    }

    private void AddHeaders(HttpRequestMessage message, string? fileName = null, bool includePrivateScanning = false)
    {
        message.Headers.Add("apikey", _apiKey);
        if (!string.IsNullOrWhiteSpace(fileName))
            message.Headers.Add("filename", fileName.Replace("\r", string.Empty).Replace("\n", string.Empty));
        if (includePrivateScanning && _privateScanning)
            message.Headers.Add("samplesharing", "0");
    }

    private static int? ReadInt(JsonElement root, string parent, string property)
    {
        return root.TryGetProperty(parent, out var parentElement) && parentElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var integer)
            ? integer : null;
    }
}
