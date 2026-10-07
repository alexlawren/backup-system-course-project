using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backup.Shared.Protocol;

public class ProtocolFrameReader
{
    public static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly JsonSerializerOptions _jsonOptions;

    public ProtocolFrameReader(JsonSerializerOptions? jsonOptions = null)
    {
        _jsonOptions = jsonOptions ?? DefaultJsonOptions;
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from the stream into the provided buffer.
    /// Loops until the buffer is filled, handling fragmented TCP chunks.
    /// Throws <see cref="ProtocolException"/> if the stream reaches EOF before the buffer is filled.
    /// </summary>
    public static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        bool filled = await TryReadExactlyAsync(stream, buffer, cancellationToken);
        if (!filled)
        {
            throw new ProtocolException($"Unexpected end of stream. Expected {buffer.Length} bytes, but received EOF.");
        }
    }

    /// <summary>
    /// Attempts to read exactly <paramref name="buffer"/>.Length bytes from the stream.
    /// If 0 bytes have been read and EOF is encountered, returns false (clean peer disconnect).
    /// If partial bytes have been read and EOF is encountered, throws <see cref="ProtocolException"/>.
    /// </summary>
    public static async Task<bool> TryReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                if (totalRead == 0)
                {
                    return false;
                }
                throw new ProtocolException($"Unexpected end of stream while reading frame data. Expected {buffer.Length} bytes, but stream terminated after {totalRead} bytes.");
            }
            totalRead += read;
        }

        return true;
    }

    /// <summary>
    /// Reads the framing header. Returns null if stream ended cleanly before any header bytes were received.
    /// Throws <see cref="ProtocolException"/> if frame is malformed or invalid.
    /// </summary>
    public async Task<ProtocolHeader?> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] lengthBuffer = new byte[ProtocolConstants.HeaderLengthFieldSize];
        bool hasBytes = await TryReadExactlyAsync(stream, lengthBuffer, cancellationToken);
        if (!hasBytes)
        {
            return null;
        }

        uint headerLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
        if (headerLength == 0)
        {
            throw new ProtocolException("Invalid frame framing: HeaderLength cannot be 0.");
        }

        if (headerLength > ProtocolConstants.MaxHeaderLength)
        {
            throw new ProtocolException($"Invalid frame framing: HeaderLength ({headerLength} bytes) exceeds maximum allowed limit of {ProtocolConstants.MaxHeaderLength} bytes (64 KiB).");
        }

        byte[] headerBytes = new byte[headerLength];
        await ReadExactlyAsync(stream, headerBytes, cancellationToken);

        ProtocolHeader? header;
        try
        {
            header = JsonSerializer.Deserialize<ProtocolHeader>(headerBytes, _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"Failed to deserialize protocol header JSON: {ex.Message}", ex);
        }

        if (header == null)
        {
            throw new ProtocolException("Deserialized protocol header is null.");
        }

        header.Validate();
        return header;
    }

    /// <summary>
    /// Streams <paramref name="bodyLength"/> bytes from <paramref name="sourceStream"/> to <paramref name="destinationStream"/>
    /// in chunks without loading the entire body into memory.
    /// </summary>
    public async Task ReadBodyToStreamAsync(
        Stream sourceStream,
        Stream destinationStream,
        long bodyLength,
        CancellationToken cancellationToken = default,
        int bufferSize = 65536)
    {
        if (bodyLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bodyLength), "Body length cannot be negative.");
        }

        if (bodyLength == 0)
        {
            return;
        }

        int effectiveBufferSize = (int)Math.Min(bufferSize, Math.Min(bodyLength, 1024 * 1024));
        byte[] buffer = new byte[effectiveBufferSize];
        long remaining = bodyLength;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await sourceStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
            {
                throw new ProtocolException($"Unexpected end of stream while streaming body data. Remaining expected bytes: {remaining}.");
            }

            await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    /// <summary>
    /// Reads small body into a byte array. For large bodies, use <see cref="ReadBodyToStreamAsync"/>.
    /// </summary>
    public async Task<byte[]> ReadBodyBytesAsync(Stream stream, long bodyLength, CancellationToken cancellationToken = default)
    {
        if (bodyLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bodyLength), "Body length cannot be negative.");
        }

        if (bodyLength > int.MaxValue)
        {
            throw new ProtocolException($"Body length {bodyLength} is too large for an in-memory byte array.");
        }

        if (bodyLength == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] bodyBytes = new byte[bodyLength];
        await ReadExactlyAsync(stream, bodyBytes, cancellationToken);
        return bodyBytes;
    }

    /// <summary>
    /// Reads and deserializes JSON body payload.
    /// </summary>
    public async Task<T?> ReadJsonBodyAsync<T>(Stream stream, long bodyLength, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ReadBodyBytesAsync(stream, bodyLength, cancellationToken);
        if (bytes.Length == 0)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(bytes, _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"Failed to deserialize body JSON for type {typeof(T).Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads the entire frame (header + body bytes) into memory. Intended for control messages and small frames.
    /// </summary>
    public async Task<(ProtocolHeader? Header, byte[] Body)> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = await ReadHeaderAsync(stream, cancellationToken);
        if (header == null)
        {
            return (null, Array.Empty<byte>());
        }

        var body = await ReadBodyBytesAsync(stream, header.BodyLength, cancellationToken);
        return (header, body);
    }
}
