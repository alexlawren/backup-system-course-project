using Backup.Shared.Storage;
using Xunit;

namespace Backup.Tests;

public class ConflictFileNameHelperTests
{
    [Theory]
    [InlineData("DESKTOP-B", "DESKTOP-B")]
    [InlineData("DESKTOP B", "DESKTOP_B")]
    [InlineData("DESK/TOP:B?*", "DESK_TOP_B")]
    [InlineData("", "CLIENT")]
    [InlineData("   ", "CLIENT")]
    [InlineData(null, "CLIENT")]
    public void SanitizeMachineName_CleansInvalidCharacters(string? input, string expected)
    {
        string sanitized = ConflictFileNameHelper.SanitizeMachineName(input);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void GenerateConflictFileName_StandardFileWithExtension()
    {
        var timestamp = new DateTime(2026, 10, 16, 14, 23, 55, DateTimeKind.Utc);
        string result = ConflictFileNameHelper.GenerateConflictFileName("report.txt", "DESKTOP-B", timestamp);
        Assert.Equal("report (conflict DESKTOP-B 2026-10-16 142355).txt", result);
    }

    [Fact]
    public void GenerateConflictFileName_FileWithoutExtension()
    {
        var timestamp = new DateTime(2026, 10, 16, 14, 23, 55, DateTimeKind.Utc);
        string result = ConflictFileNameHelper.GenerateConflictFileName("README", "DESKTOP-B", timestamp);
        Assert.Equal("README (conflict DESKTOP-B 2026-10-16 142355)", result);
    }

    [Fact]
    public void GenerateConflictFileName_CompoundExtensionTarGz()
    {
        var timestamp = new DateTime(2026, 10, 16, 14, 23, 55, DateTimeKind.Utc);
        string result = ConflictFileNameHelper.GenerateConflictFileName("archive.tar.gz", "DESKTOP-B", timestamp);
        Assert.Equal("archive (conflict DESKTOP-B 2026-10-16 142355).tar.gz", result);
    }

    [Fact]
    public void ResolveUniqueConflictRelativePath_CollisionIncrementsSuffix()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "conflict_helper_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var timestamp = new DateTime(2026, 10, 16, 14, 23, 55, DateTimeKind.Utc);
            string baseCandidate = ConflictFileNameHelper.GenerateConflictFileName("notes.txt", "CLIENT-A", timestamp);

            // First time: candidate does not exist
            string rel1 = ConflictFileNameHelper.ResolveUniqueConflictRelativePath(tempDir, "notes.txt", "CLIENT-A", timestamp);
            Assert.Equal(baseCandidate, rel1);

            // Create file at candidate
            File.WriteAllText(Path.Combine(tempDir, rel1), "first conflict copy");

            // Second time: collision detected, should add (2)
            string rel2 = ConflictFileNameHelper.ResolveUniqueConflictRelativePath(tempDir, "notes.txt", "CLIENT-A", timestamp);
            Assert.Equal("notes (conflict CLIENT-A 2026-10-16 142355) (2).txt", rel2);

            // Create file at (2)
            File.WriteAllText(Path.Combine(tempDir, rel2), "second conflict copy");

            // Third time: should add (3)
            string rel3 = ConflictFileNameHelper.ResolveUniqueConflictRelativePath(tempDir, "notes.txt", "CLIENT-A", timestamp);
            Assert.Equal("notes (conflict CLIENT-A 2026-10-16 142355) (3).txt", rel3);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveUniqueConflictRelativePath_NestedDirectory_PreservesDirectory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "conflict_helper_nested_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "sub", "folder"));
        try
        {
            var timestamp = new DateTime(2026, 10, 16, 14, 23, 55, DateTimeKind.Utc);
            string rel = ConflictFileNameHelper.ResolveUniqueConflictRelativePath(tempDir, "sub/folder/file.doc", "PC1", timestamp);
            Assert.Equal("sub/folder/file (conflict PC1 2026-10-16 142355).doc", rel);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
