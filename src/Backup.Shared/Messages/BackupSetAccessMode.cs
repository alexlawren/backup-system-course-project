using System.Text.Json.Serialization;

namespace Backup.Shared.Messages;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackupSetAccessMode
{
    Private,
    Shared
}
