namespace Backup.Client.Web.Services;

public interface IClipboardService
{
    Task<bool> CopyTextAsync(string? text);
}
