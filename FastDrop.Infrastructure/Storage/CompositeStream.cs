using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FastDrop.Infrastructure.Storage;

/// <summary>
/// A forward-only stream that seamlessly reads across multiple physical files.
/// Eliminates the need to assemble chunks into a single large file.
/// </summary>
public class CompositeStream : Stream
{
    private readonly IReadOnlyList<string> _filePaths;
    private int _currentFileIndex = 0;
    private FileStream? _currentStream;
    private readonly long _totalLength;
    private long _position;

    public CompositeStream(IReadOnlyList<string> filePaths)
    {
        _filePaths = filePaths ?? throw new ArgumentNullException(nameof(filePaths));
        // Pre-calculate total length so ASP.NET Core can set Content-Length header
        _totalLength = 0;
        foreach (var path in _filePaths)
        {
            _totalLength += new FileInfo(path).Length;
        }
        _position = 0;
        OpenNextStream();
    }

    private void OpenNextStream()
    {
        _currentStream?.Dispose();
        _currentStream = null;

        if (_currentFileIndex < _filePaths.Count)
        {
            var path = _filePaths[_currentFileIndex];
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Missing chunk file: {path}");
            }
            
            _currentStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
            _currentFileIndex++;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_currentStream == null)
            return 0; // EOF

        int bytesRead = await _currentStream.ReadAsync(buffer, cancellationToken);
        
        // If we hit the end of the current chunk, open the next one and keep reading
        if (bytesRead == 0)
        {
            OpenNextStream();
            return await ReadAsync(buffer, cancellationToken);
        }

        _position += bytesRead;
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_currentStream == null)
            return 0; // EOF

        int bytesRead = await _currentStream.ReadAsync(buffer, offset, count, cancellationToken);
        
        if (bytesRead == 0)
        {
            OpenNextStream();
            return await ReadAsync(buffer, offset, count, cancellationToken);
        }

        _position += bytesRead;
        return bytesRead;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_currentStream == null)
            return 0;

        int bytesRead = _currentStream.Read(buffer, offset, count);
        
        if (bytesRead == 0)
        {
            OpenNextStream();
            return Read(buffer, offset, count);
        }

        _position += bytesRead;
        return bytesRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _currentStream?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_currentStream != null)
        {
            await _currentStream.DisposeAsync();
        }
        await base.DisposeAsync();
    }

    // Stream metadata — Length is required for Content-Length header
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _totalLength;
    public override long Position 
    { 
        get => _position; 
        set => throw new NotSupportedException(); 
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
