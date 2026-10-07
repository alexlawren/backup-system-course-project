using System.Text.Json.Serialization;

namespace Backup.Shared.Protocol;

public class ProtocolHeader
{
    public string Magic { get; set; } = ProtocolConstants.Magic;

    public int ProtocolVersion { get; set; } = ProtocolConstants.Version;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MessageType MessageType { get; set; }

    public Guid MessageId { get; set; } = Guid.NewGuid();

    public Guid? CorrelationId { get; set; }

    public string ContentType { get; set; } = ProtocolContentTypes.ApplicationJson;

    public long BodyLength { get; set; }

    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;

    public void Validate()
    {
        if (Magic != ProtocolConstants.Magic)
        {
            throw new ProtocolException($"Invalid magic: expected '{ProtocolConstants.Magic}', got '{Magic}'.");
        }

        if (ProtocolVersion != ProtocolConstants.Version)
        {
            throw new ProtocolException($"Unsupported protocol version: expected {ProtocolConstants.Version}, got {ProtocolVersion}.");
        }

        if (BodyLength < 0)
        {
            throw new ProtocolException($"Invalid BodyLength: {BodyLength} (must be non-negative).");
        }

        if (string.IsNullOrWhiteSpace(ContentType))
        {
            throw new ProtocolException("Header ContentType cannot be null or empty.");
        }

        if (MessageId == Guid.Empty)
        {
            throw new ProtocolException("Header MessageId cannot be an empty GUID.");
        }

        if (SentAtUtc == default)
        {
            throw new ProtocolException("Header SentAtUtc must be a valid non-default timestamp.");
        }
    }
}
