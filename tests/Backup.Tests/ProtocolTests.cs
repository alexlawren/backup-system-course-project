using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Xunit;

namespace Backup.Tests;

public class ProtocolTests
{
    private readonly ProtocolFrameReader _reader = new();
    private readonly ProtocolFrameWriter _writer = new();

    [Fact]
    public async Task JsonControlFrame_RoundTrip_PreservesData()
    {
        using var stream = new MemoryStream();
        var helloReq = new HelloRequest
        {
            ClientName = "Backup.Client.Web",
            Version = "1.0.0"
        };
        var correlationId = Guid.NewGuid();

        var writtenHeader = await _writer.WriteJsonFrameAsync(
            stream,
            MessageType.Hello,
            helloReq,
            correlationId);

        stream.Position = 0;

        var readHeader = await _reader.ReadHeaderAsync(stream);
        Assert.NotNull(readHeader);
        Assert.Equal(MessageType.Hello, readHeader.MessageType);
        Assert.Equal(writtenHeader.MessageId, readHeader.MessageId);
        Assert.Equal(correlationId, readHeader.CorrelationId);
        Assert.Equal(ProtocolContentTypes.ApplicationJson, readHeader.ContentType);
        Assert.True(readHeader.BodyLength > 0);

        var body = await _reader.ReadJsonBodyAsync<HelloRequest>(stream, readHeader.BodyLength);
        Assert.NotNull(body);
        Assert.Equal("Backup.Client.Web", body.ClientName);
        Assert.Equal("1.0.0", body.Version);
    }

    [Fact]
    public async Task Frame_WithZeroBodyLength_RoundTrip_Succeeds()
    {
        using var stream = new MemoryStream();
        var correlationId = Guid.NewGuid();

        var writtenHeader = await _writer.WriteControlFrameAsync(
            stream,
            MessageType.Ping,
            correlationId);

        stream.Position = 0;

        var readHeader = await _reader.ReadHeaderAsync(stream);
        Assert.NotNull(readHeader);
        Assert.Equal(MessageType.Ping, readHeader.MessageType);
        Assert.Equal(0, readHeader.BodyLength);
        Assert.Equal(correlationId, readHeader.CorrelationId);

        var bodyBytes = await _reader.ReadBodyBytesAsync(stream, readHeader.BodyLength);
        Assert.Empty(bodyBytes);
    }

    [Fact]
    public async Task MessageId_PreservedExactlyAcrossRoundTrip()
    {
        using var stream = new MemoryStream();
        var expectedId = Guid.NewGuid();
        var header = new ProtocolHeader
        {
            Magic = ProtocolConstants.Magic,
            ProtocolVersion = ProtocolConstants.Version,
            MessageType = MessageType.Pong,
            MessageId = expectedId,
            CorrelationId = null,
            ContentType = ProtocolContentTypes.ApplicationJson,
            BodyLength = 0,
            SentAtUtc = DateTime.UtcNow
        };

        await _writer.WriteHeaderAsync(stream, header);

        stream.Position = 0;
        var readHeader = await _reader.ReadHeaderAsync(stream);

        Assert.NotNull(readHeader);
        Assert.Equal(expectedId, readHeader.MessageId);
    }

    [Fact]
    public async Task CorrelationId_PreservedExactlyAcrossRoundTrip()
    {
        using var stream = new MemoryStream();
        var correlationId = Guid.NewGuid();
        var ping = new PingMessage { Timestamp = 123456789 };

        await _writer.WriteJsonFrameAsync(stream, MessageType.Ping, ping, correlationId);

        stream.Position = 0;
        var readHeader = await _reader.ReadHeaderAsync(stream);

        Assert.NotNull(readHeader);
        Assert.Equal(correlationId, readHeader.CorrelationId);
    }

    [Fact]
    public async Task InvalidMagic_ThrowsProtocolException()
    {
        using var stream = new MemoryStream();
        var header = new ProtocolHeader
        {
            Magic = "WRNG",
            ProtocolVersion = 1,
            MessageType = MessageType.Hello,
            MessageId = Guid.NewGuid(),
            ContentType = ProtocolContentTypes.ApplicationJson,
            BodyLength = 0,
            SentAtUtc = DateTime.UtcNow
        };

        // Write header manually bypass writer's header validation to test reader validation
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(header, ProtocolFrameWriter.DefaultJsonOptions);
        byte[] lenBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)jsonBytes.Length);
        await stream.WriteAsync(lenBuf);
        await stream.WriteAsync(jsonBytes);

        stream.Position = 0;
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => _reader.ReadHeaderAsync(stream));
        Assert.Contains("Invalid magic", ex.Message);
    }

    [Fact]
    public async Task UnsupportedProtocolVersion_ThrowsProtocolException()
    {
        using var stream = new MemoryStream();
        var header = new ProtocolHeader
        {
            Magic = ProtocolConstants.Magic,
            ProtocolVersion = 99,
            MessageType = MessageType.Hello,
            MessageId = Guid.NewGuid(),
            ContentType = ProtocolContentTypes.ApplicationJson,
            BodyLength = 0,
            SentAtUtc = DateTime.UtcNow
        };

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(header, ProtocolFrameWriter.DefaultJsonOptions);
        byte[] lenBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)jsonBytes.Length);
        await stream.WriteAsync(lenBuf);
        await stream.WriteAsync(jsonBytes);

        stream.Position = 0;
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => _reader.ReadHeaderAsync(stream));
        Assert.Contains("Unsupported protocol version", ex.Message);
    }

    [Fact]
    public async Task HeaderExceeding64KiB_ThrowsProtocolException()
    {
        using var stream = new MemoryStream();
        uint oversizedLength = 64 * 1024 + 1; // 65537 bytes

        byte[] lenBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, oversizedLength);
        await stream.WriteAsync(lenBuf);

        stream.Position = 0;
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => _reader.ReadHeaderAsync(stream));
        Assert.Contains("exceeds maximum allowed limit", ex.Message);
    }

    [Fact]
    public async Task HeaderLengthZero_ThrowsProtocolException()
    {
        using var stream = new MemoryStream();
        byte[] lenBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, 0);
        await stream.WriteAsync(lenBuf);

        stream.Position = 0;
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => _reader.ReadHeaderAsync(stream));
        Assert.Contains("cannot be 0", ex.Message);
    }

    [Fact]
    public async Task BinaryBody_StreamingRoundTrip_PreservesAllBytesWithoutCorruption()
    {
        // 512 KiB random binary payload to test streaming
        byte[] originalPayload = new byte[512 * 1024];
        new Random(42).NextBytes(originalPayload);

        var header = new ProtocolHeader
        {
            Magic = ProtocolConstants.Magic,
            ProtocolVersion = ProtocolConstants.Version,
            MessageType = MessageType.StoreFile,
            MessageId = Guid.NewGuid(),
            ContentType = ProtocolContentTypes.ApplicationOctetStream,
            BodyLength = originalPayload.Length,
            SentAtUtc = DateTime.UtcNow
        };

        using var networkSimulationStream = new MemoryStream();

        // Write streaming body directly from source stream
        using (var sourcePayloadStream = new MemoryStream(originalPayload))
        {
            await _writer.WriteFrameAsync(networkSimulationStream, header, sourcePayloadStream);
        }

        networkSimulationStream.Position = 0;

        // Read header
        var readHeader = await _reader.ReadHeaderAsync(networkSimulationStream);
        Assert.NotNull(readHeader);
        Assert.Equal(MessageType.StoreFile, readHeader.MessageType);
        Assert.Equal(originalPayload.Length, readHeader.BodyLength);
        Assert.Equal(ProtocolContentTypes.ApplicationOctetStream, readHeader.ContentType);

        // Stream body to destination stream
        using var destinationStream = new MemoryStream();
        await _reader.ReadBodyToStreamAsync(networkSimulationStream, destinationStream, readHeader.BodyLength);

        byte[] receivedPayload = destinationStream.ToArray();
        Assert.Equal(originalPayload.Length, receivedPayload.Length);
        Assert.True(originalPayload.AsSpan().SequenceEqual(receivedPayload));
    }

    [Fact]
    public async Task Reader_HandlesFragmentedStream_ReadsFrameCorrectly()
    {
        // This test simulates TCP fragmentation where ReadAsync returns only tiny chunks (e.g. 1-3 bytes)
        using var memStream = new MemoryStream();

        var request = new HelloRequest
        {
            ClientName = "FragmentedClientSimulator",
            Version = "2.0"
        };
        var correlationId = Guid.NewGuid();

        await _writer.WriteJsonFrameAsync(memStream, MessageType.Hello, request, correlationId);
        memStream.Position = 0;

        // Wrap stream in ChunkedReadStream that returns at most 2 bytes per ReadAsync
        using var chunkedStream = new ChunkedReadStream(memStream, maxChunkSize: 2);

        var readHeader = await _reader.ReadHeaderAsync(chunkedStream);
        Assert.NotNull(readHeader);
        Assert.Equal(MessageType.Hello, readHeader.MessageType);
        Assert.Equal(correlationId, readHeader.CorrelationId);

        var body = await _reader.ReadJsonBodyAsync<HelloRequest>(chunkedStream, readHeader.BodyLength);
        Assert.NotNull(body);
        Assert.Equal("FragmentedClientSimulator", body.ClientName);
        Assert.Equal("2.0", body.Version);
    }

    [Fact]
    public async Task CleanEndOfStream_ReturnsNullHeader()
    {
        using var emptyStream = new MemoryStream();
        var header = await _reader.ReadHeaderAsync(emptyStream);
        Assert.Null(header);
    }

    [Fact]
    public async Task IncompleteHeaderData_ThrowsProtocolException()
    {
        using var stream = new MemoryStream();
        // 4 bytes length prefix indicating 50 bytes header, but stream ends after 10 bytes
        byte[] lenBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, 50);
        await stream.WriteAsync(lenBuf);
        await stream.WriteAsync(new byte[10]);

        stream.Position = 0;
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => _reader.ReadHeaderAsync(stream));
        Assert.Contains("Unexpected end of stream", ex.Message);
    }
}

/// <summary>
/// Stream decorator that simulates fragmented TCP reads by restricting the maximum number of bytes returned per read.
/// </summary>
public class ChunkedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly int _maxChunkSize;

    public ChunkedReadStream(Stream inner, int maxChunkSize)
    {
        _inner = inner;
        _maxChunkSize = maxChunkSize;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int toRead = Math.Min(count, _maxChunkSize);
        return _inner.Read(buffer, offset, toRead);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int toRead = Math.Min(buffer.Length, _maxChunkSize);
        return await _inner.ReadAsync(buffer[..toRead], cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int toRead = Math.Min(count, _maxChunkSize);
        return _inner.ReadAsync(buffer, offset, toRead, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
