using Backup.Shared.Protocol;
using Backup.Shared.Validation;
using Xunit;

namespace Backup.Tests;

public class PathValidationTests
{
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("docs/../../etc/passwd")]
    [InlineData("folder/..")]
    [InlineData("./relative.txt")]
    [InlineData("folder/./sub/file.txt")]
    public void PathTraversal_Rejected(string path)
    {
        bool isValid = ProtocolPathValidator.TryNormalizeAndValidate(path, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("traversal", error, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ProtocolException>(() => ProtocolPathValidator.NormalizeAndValidate(path));
    }

    [Theory]
    [InlineData("/absolute/path.txt")]
    [InlineData("/root")]
    [InlineData("//server/share/file.txt")]
    public void AbsolutePath_Rejected(string path)
    {
        bool isValid = ProtocolPathValidator.TryNormalizeAndValidate(path, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("Absolute", error, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ProtocolException>(() => ProtocolPathValidator.NormalizeAndValidate(path));
    }

    [Theory]
    [InlineData("C:\\Windows\\system32")]
    [InlineData("D:/projects/file.txt")]
    [InlineData("z:\\data.bin")]
    public void DriveRootedPath_Rejected(string path)
    {
        bool isValid = ProtocolPathValidator.TryNormalizeAndValidate(path, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("Drive-rooted", error, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ProtocolException>(() => ProtocolPathValidator.NormalizeAndValidate(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespacePath_Rejected(string? path)
    {
        bool isValid = ProtocolPathValidator.TryNormalizeAndValidate(path, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NullByteInPath_Rejected()
    {
        string path = "docs\0/file.txt";
        bool isValid = ProtocolPathValidator.TryNormalizeAndValidate(path, out _, out var error);
        Assert.False(isValid);
        Assert.Contains("forbidden", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("file.txt", "file.txt")]
    [InlineData("folder\\sub\\file.txt", "folder/sub/file.txt")]
    [InlineData("  docs/report.pdf  ", "docs/report.pdf")]
    [InlineData("a/b/c/d/e.txt", "a/b/c/d/e.txt")]
    public void ValidRelativePath_NormalizedCorrectly(string input, string expected)
    {
        bool isValid = ProtocolPathValidator.TryNormalizeAndValidate(input, out var normalized, out var error);
        Assert.True(isValid, error);
        Assert.Equal(expected, normalized);
    }
}
