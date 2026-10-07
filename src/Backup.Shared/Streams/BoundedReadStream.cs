namespace Backup.Shared.Streams;

/// <summary>
/// A non-owning stream wrapper that limits reading from an underlying stream
/// to exactly <see cref="Length"/> bytes without loading the payload into memory.
/// Disposing this stream does NOT dispose the underlying stream.
/// </summary>
public class BoundedReadStream : Stream
{
    private readonly Stream _underlyingStream;
    private readonly long _length;
    private long _bytesRead;

    public BoundedReadStream(Stream underlyingStream, long length)
    {
        ArgumentNullException.ThrowIfNull(underlyingStream);
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
        }

        _underlyingStream = underlyingStream;
        _length = length;
        _bytesRead = 0;
    }

    public override bool CanRead => _underlyingStream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException("Seeking is not supported on BoundedReadStream.");
    }

    public long RemainingBytes => Math.Max(0, _length - _bytesRead);

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_bytesRead >= _length)
        {
            return 0;
        }

        int toRead = (int)Math.Min(count, _length - _bytesRead);
        int read = _underlyingStream.Read(buffer, offset, toRead);
        _bytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_bytesRead >= _length)
        {
            return 0;
        }

        int toRead = (int)Math.Min(buffer.Length, _length - _bytesRead);
        int read = await _underlyingStream.ReadAsync(buffer[..toRead], cancellationToken);
        _bytesRead += read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_bytesRead >= _length)
        {
            return 0;
        }

        int toRead = (int)Math.Min(count, _length - _bytesRead);
        int read = await _underlyingStream.ReadAsync(buffer.AsMemory(offset, toRead), cancellationToken);
        _bytesRead += read;
        return read;
    }

    public override void Flush() => _underlyingStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _underlyingStream.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("Seeking is not supported.");
    public override void SetLength(long value) => throw new NotSupportedException("Setting length is not supported.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Writing is not supported.");

    protected override void Dispose(bool disposing)
    {
        // Non-owning: do not dispose _underlyingStream
        base.Dispose(disposing);
    }
}
