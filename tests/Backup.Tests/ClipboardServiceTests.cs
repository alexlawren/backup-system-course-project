using Backup.Client.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace Backup.Tests;

public class ClipboardServiceTests
{
    private class FakeJsRuntime : IJSRuntime
    {
        public string? LastIdentifier { get; private set; }
        public object?[]? LastArgs { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            LastIdentifier = identifier;
            LastArgs = args;
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            LastIdentifier = identifier;
            LastArgs = args;
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    [Fact]
    public async Task CopyShareCode_ButtonUsesFullShareCode()
    {
        var js = new FakeJsRuntime();
        var clipboard = new ClipboardService(js, NullLogger<ClipboardService>.Instance);

        string rawCode = "  RR5T-WEM7  ";
        bool result = await clipboard.CopyTextAsync(rawCode);

        Assert.True(result);
        Assert.Equal("navigator.clipboard.writeText", js.LastIdentifier);
        Assert.NotNull(js.LastArgs);
        Assert.Single(js.LastArgs);
        Assert.Equal("RR5T-WEM7", js.LastArgs[0]);
    }

    [Fact]
    public async Task CopyClientId_ButtonUsesFullClientId()
    {
        var js = new FakeJsRuntime();
        var clipboard = new ClipboardService(js, NullLogger<ClipboardService>.Instance);

        var fullGuid = Guid.NewGuid();
        string guidStr = fullGuid.ToString();

        bool result = await clipboard.CopyTextAsync(guidStr);

        Assert.True(result);
        Assert.Equal("navigator.clipboard.writeText", js.LastIdentifier);
        Assert.NotNull(js.LastArgs);
        Assert.Equal(guidStr, js.LastArgs[0]);
    }

    [Fact]
    public async Task CopySha256_ButtonUsesFullHash()
    {
        var js = new FakeJsRuntime();
        var clipboard = new ClipboardService(js, NullLogger<ClipboardService>.Instance);

        string fullHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        Assert.Equal(64, fullHash.Length);

        bool result = await clipboard.CopyTextAsync(fullHash);

        Assert.True(result);
        Assert.Equal("navigator.clipboard.writeText", js.LastIdentifier);
        Assert.NotNull(js.LastArgs);
        Assert.Equal(fullHash, js.LastArgs[0]);
        Assert.Equal(64, js.LastArgs[0]?.ToString()?.Length);
    }
}
