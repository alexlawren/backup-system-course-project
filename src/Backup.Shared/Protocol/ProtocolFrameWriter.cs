using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backup.Shared.Protocol;

public class ProtocolFrameWriter
{
    public static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly JsonSerializerOptions _jsonOptions;

    public ProtocolFrameWriter(JsonSerializerOptions? jsonOptions = null)
    {
        _jsonOptions = jsonOptions ?? DefaultJsonOptions;
    }

    /// <summary>
    /// Serializes and writes the protocol header with its 4-byte BigEndian length prefix.
    /// </summary>
    public async Task WriteHeaderAsync(Stream stream, ProtocolHeader header, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(header);

        header.Validate();

        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, _jsonOptions);
        if (headerBytes.Length == 0)
        {
            throw new ProtocolException("Serialized header bytes cannot be empty.");
        }

        if (headerBytes.Length > ProtocolConstants.MaxHeaderLength)
        {
            throw new ProtocolException($"Serialized header size ({headerBytes.Length} bytes) exceeds maximum allowed limit of {ProtocolConstants.MaxHeaderLength} bytes (64 KiB).");
        }

        byte[] lengthBuffer = new byte[ProtocolConstants.HeaderLengthFieldSize];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBuffer, (uint)headerBytes.Length);

        await stream.WriteAsync(lengthBuffer, cancellationToken);
        await stream.WriteAsync(headerBytes, cancellationToken);
    }

    /// <summary>
    /// Writes a complete frame with header and streams <paramref name="header"/>.BodyLength bytes from <paramref name="bodyStream"/>
    /// directly to <paramref name="stream"/> without buffering the entire body into memory.
    /// </summary>
    public async Task WriteFrameAsync(
        Stream stream,
        ProtocolHeader header,
        Stream bodyStream,
        CancellationToken cancellationToken = default,
        int bufferSize = 65536)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(bodyStream);

        await WriteHeaderAsync(stream, header, cancellationToken);

        if (header.BodyLength == 0)
        {
            await stream.FlushAsync(cancellationToken);
            return;
        }

        int effectiveBufferSize = (int)Math.Min(bufferSize, Math.Min(header.BodyLength, 1024 * 1024));
        byte[] buffer = new byte[effectiveBufferSize];
        long remaining = header.BodyLength;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await bodyStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
            {
                throw new ProtocolException($"Body stream ended prematurely while writing frame. Remaining expected bytes: {remaining}.");
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }

        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a complete frame with header and in-memory body bytes.
    /// </summary>
    public async Task WriteFrameAsync(
        Stream stream,
        ProtocolHeader header,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(header);

        header.BodyLength = body.Length;
        await WriteHeaderAsync(stream, header, cancellationToken);

        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Serializes payload to JSON and writes a complete frame.
    /// </summary>
    public async Task<ProtocolHeader> WriteJsonFrameAsync<T>(
        Stream stream,
        MessageType messageType,
        T payload,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
        var header = new ProtocolHeader
        {
            Magic = ProtocolConstants.Magic,
            ProtocolVersion = ProtocolConstants.Version,
            MessageType = messageType,
            MessageId = Guid.NewGuid(),
            CorrelationId = correlationId,
            ContentType = ProtocolContentTypes.ApplicationJson,
            BodyLength = jsonBytes.Length,
            SentAtUtc = DateTime.UtcNow
        };

        await WriteFrameAsync(stream, header, jsonBytes, cancellationToken);
        return header;
    }

    /// <summary>
    /// Writes a control frame with BodyLength = 0 (e.g. Ping, Pong, HelloAck).
    /// </summary>
    public async Task<ProtocolHeader> WriteControlFrameAsync(
        Stream stream,
        MessageType messageType,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new ProtocolHeader
        {
            Magic = ProtocolConstants.Magic,
            ProtocolVersion = ProtocolConstants.Version,
            MessageType = messageType,
            MessageId = Guid.NewGuid(),
            CorrelationId = correlationId,
            ContentType = ProtocolContentTypes.ApplicationJson,
            BodyLength = 0,
            SentAtUtc = DateTime.UtcNow
        };

        await WriteFrameAsync(stream, header, ReadOnlyMemory<byte>.Empty, cancellationToken);
        return header;
    }
}
