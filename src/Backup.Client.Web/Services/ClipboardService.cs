using Microsoft.JSInterop;

namespace Backup.Client.Web.Services;

public class ClipboardService : IClipboardService
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<ClipboardService> _logger;

    public ClipboardService(IJSRuntime jsRuntime, ILogger<ClipboardService> logger)
    {
        _jsRuntime = jsRuntime;
        _logger = logger;
    }

    public async Task<bool> CopyTextAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            await _jsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", text.Trim());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ClipboardService] Failed to copy text to clipboard: {Message}", ex.Message);
            return false;
        }
    }
}
