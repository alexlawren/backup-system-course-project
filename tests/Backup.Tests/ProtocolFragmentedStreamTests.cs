using System.Text;
using Backup.Shared.Messages;
using Backup.Shared.Protocol;
using Xunit;

namespace Backup.Tests;

public class ProtocolFragmentedStreamTests
{
    private class ChunkedReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxChunkSize;
        private int _position;

        public ChunkedReadStream(byte[] data, int maxChunkSize)
        {
            _data = data;
            _maxChunkSize = maxChunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length) return 0;
            int toRead = Math.Min(count, Math.Min(_maxChunkSize, _data.Length - _position));
            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(1)]  // 1-byte chunks (extreme fragmentation)
    [InlineData(2)]  // 2-byte chunks
    [InlineData(5)]  // 5-byte chunks
    [InlineData(13)] // Odd chunk size
    public async Task ProtocolFrameReader_ReadsCorrectly_WhenStreamIsHeavilyFragmented(int chunkSize)
    {
        var writer = new ProtocolFrameWriter();
        var reader = new ProtocolFrameReader();

        using var memory = new MemoryStream();
        var req = new HelloRequest
        {
            ClientName = "FragmentationTestClient",
            Version = "1.0.0",
            ClientId = Guid.NewGuid()
        };
        var correlationId = Guid.NewGuid();

        var writtenHeader = await writer.WriteJsonFrameAsync(memory, MessageType.Hello, req, correlationId);
        byte[] rawBytes = memory.ToArray();

        using var fragmentedStream = new ChunkedReadStream(rawBytes, chunkSize);

        // Act
        var readHeader = await reader.ReadHeaderAsync(fragmentedStream);

        // Assert
        Assert.NotNull(readHeader);
        Assert.Equal(MessageType.Hello, readHeader.MessageType);
        Assert.Equal(writtenHeader.MessageId, readHeader.MessageId);
        Assert.Equal(correlationId, readHeader.CorrelationId);
        Assert.Equal(writtenHeader.BodyLength, readHeader.BodyLength);

        var body = await reader.ReadJsonBodyAsync<HelloRequest>(fragmentedStream, readHeader.BodyLength);
        Assert.NotNull(body);
        Assert.Equal("FragmentationTestClient", body.ClientName);
        Assert.Equal("1.0.0", body.Version);
        Assert.Equal(req.ClientId, body.ClientId);
    }
}
