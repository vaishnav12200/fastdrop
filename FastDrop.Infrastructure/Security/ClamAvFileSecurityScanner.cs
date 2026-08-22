using System.Buffers;
using System.Net.Sockets;
using System.Text;
using FastDrop.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FastDrop.Infrastructure.Security;

/// <summary>Streams quarantined content to clamd using its INSTREAM protocol.</summary>
public sealed class ClamAvFileSecurityScanner : IFileSecurityScanner
{
    private const int BufferSize = 64 * 1024;
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<ClamAvFileSecurityScanner> _logger;

    public ClamAvFileSecurityScanner(IConfiguration configuration, ILogger<ClamAvFileSecurityScanner> logger)
    {
        _host = configuration["MalwareScanner:Host"] ?? "clamav";
        _port = int.TryParse(configuration["MalwareScanner:Port"], out var configuredPort) ? configuredPort : 3310;
        _logger = logger;
    }

    public async Task<FileScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, cancellationToken);
            await using var network = client.GetStream();
            await network.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), cancellationToken);

            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            var lengthBuffer = new byte[4];
            try
            {
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                {
                    lengthBuffer[0] = (byte)(read >> 24);
                    lengthBuffer[1] = (byte)(read >> 16);
                    lengthBuffer[2] = (byte)(read >> 8);
                    lengthBuffer[3] = (byte)read;
                    await network.WriteAsync(lengthBuffer, cancellationToken);
                    await network.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await network.WriteAsync(new byte[4], cancellationToken);
                await network.FlushAsync(cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var response = await ReadResponseAsync(network, cancellationToken);
            if (response.Contains("OK", StringComparison.OrdinalIgnoreCase))
                return new FileScanResult(FileScanVerdict.Clean);
            if (response.Contains("FOUND", StringComparison.OrdinalIgnoreCase))
                return new FileScanResult(FileScanVerdict.ThreatFound, response);

            _logger.LogWarning("ClamAV returned an unexpected scan response: {Response}", response);
            return new FileScanResult(FileScanVerdict.Unavailable, response);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "ClamAV is unavailable; keeping the file quarantined.");
            return new FileScanResult(FileScanVerdict.Unavailable, "Scanner unavailable");
        }
    }

    private static async Task<string> ReadResponseAsync(NetworkStream network, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var read = await network.ReadAsync(buffer, cancellationToken);
        return read == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\0', '\r', '\n');
    }
}
