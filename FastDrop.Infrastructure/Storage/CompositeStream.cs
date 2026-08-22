namespace FastDrop.Infrastructure.Storage;

/// <summary>
/// Presents separately stored chunks as one seekable file. Seeking enables HTTP
/// Range requests, so browsers can resume an interrupted large download.
/// </summary>
public sealed class CompositeStream : Stream
{
    private const int StreamBufferSize = 1024 * 1024;
    private readonly IReadOnlyList<string> _filePaths;
    private readonly long[] _chunkStarts;
    private int _currentFileIndex = -1;
    private FileStream? _currentStream;
    private readonly long _totalLength;
    private long _position;

    public CompositeStream(IReadOnlyList<string> filePaths)
    {
        if (filePaths is null || filePaths.Count == 0)
            throw new ArgumentException("At least one chunk path is required.", nameof(filePaths));

        _filePaths = filePaths;
        _chunkStarts = new long[filePaths.Count];
        long position = 0;

        for (var i = 0; i < filePaths.Count; i++)
        {
            var path = filePaths[i];
            if (!File.Exists(path))
                throw new FileNotFoundException($"Missing chunk file: {path}");

            _chunkStarts[i] = position;
            position = checked(position + new FileInfo(path).Length);
        }

        _totalLength = position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        var totalBytesRead = 0;

        while (_position < _totalLength)
        {
            EnsureStreamAtPosition();
            var bytesRead = _currentStream!.Read(buffer[totalBytesRead..]);
            if (bytesRead > 0)
            {
                _position += bytesRead;
                totalBytesRead += bytesRead;
                if (totalBytesRead == buffer.Length)
                    return totalBytesRead;

                continue;
            }

            CloseCurrentStream();
        }

        return totalBytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;

        var totalBytesRead = 0;

        while (_position < _totalLength)
        {
            EnsureStreamAtPosition();
            var bytesRead = await _currentStream!.ReadAsync(buffer[totalBytesRead..], cancellationToken);
            if (bytesRead > 0)
            {
                _position += bytesRead;
                totalBytesRead += bytesRead;
                if (totalBytesRead == buffer.Length)
                    return totalBytesRead;

                continue;
            }

            await CloseCurrentStreamAsync();
        }

        return totalBytesRead;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_totalLength + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0 || target > _totalLength)
            throw new IOException("Attempted to seek outside the composite file.");

        _position = target;
        CloseCurrentStream();
        return _position;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseCurrentStream();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseCurrentStreamAsync();
        await base.DisposeAsync();
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _totalLength;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void EnsureStreamAtPosition()
    {
        var desiredFileIndex = GetFileIndex(_position);
        if (_currentStream is not null && _currentFileIndex == desiredFileIndex)
            return;

        CloseCurrentStream();
        _currentStream = new FileStream(_filePaths[desiredFileIndex], FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: StreamBufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
        _currentStream.Position = _position - _chunkStarts[desiredFileIndex];
        _currentFileIndex = desiredFileIndex;
    }

    private int GetFileIndex(long position)
    {
        if (position < 0 || position >= _totalLength)
            throw new ArgumentOutOfRangeException(nameof(position));

        var index = Array.BinarySearch(_chunkStarts, position);
        return index >= 0 ? index : ~index - 1;
    }

    private void CloseCurrentStream()
    {
        _currentStream?.Dispose();
        _currentStream = null;
        _currentFileIndex = -1;
    }

    private async ValueTask CloseCurrentStreamAsync()
    {
        if (_currentStream is not null)
            await _currentStream.DisposeAsync();
        _currentStream = null;
        _currentFileIndex = -1;
    }
}
