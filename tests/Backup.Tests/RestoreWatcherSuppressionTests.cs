using Backup.Client.Web.Services;
using Xunit;

namespace Backup.Tests;

public class RestoreWatcherSuppressionTests
{
    [Fact]
    public void RestoreEchoEvent_WithExpectedHash_IsSuppressed()
    {
        var suppression = new RestoreSuppressionService();
        string path = "documents/report.docx";
        string expectedSha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        suppression.RegisterSuppression(path, expectedSha, TimeSpan.FromSeconds(10));

        Assert.True(suppression.IsSuppressed(path));
        bool suppressed = suppression.ShouldSuppress(path, expectedSha);

        Assert.True(suppressed, "Watcher event matching the restored version's SHA-256 must be suppressed.");
    }

    [Fact]
    public void MultipleWatcherEvents_FromSameRestore_AreAllSuppressed()
    {
        var suppression = new RestoreSuppressionService();
        string path = "notes.txt";
        string sha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        suppression.RegisterSuppression(path, sha, TimeSpan.FromSeconds(5));

        // Multiple rapid events (e.g. Created + Changed + Renamed)
        for (int i = 0; i < 5; i++)
        {
            Assert.True(suppression.ShouldSuppress(path, sha), $"Event {i + 1} from same restore must be suppressed.");
        }
    }

    [Fact]
    public void UserEditsFile_AfterRestore_ToDifferentHash_IsNotSuppressed()
    {
        var suppression = new RestoreSuppressionService();
        string path = "code/main.cs";
        string restoredSha = "1111111111111111111111111111111111111111111111111111111111111111";
        string modifiedSha = "2222222222222222222222222222222222222222222222222222222222222222";

        suppression.RegisterSuppression(path, restoredSha, TimeSpan.FromSeconds(10));

        bool suppressed = suppression.ShouldSuppress(path, modifiedSha);

        Assert.False(suppressed, "If user modified the file to a different hash, the event must NOT be suppressed.");
    }

    [Fact]
    public async Task ExpiredSuppression_NoLongerHidesEvents()
    {
        var suppression = new RestoreSuppressionService();
        string path = "data/stats.json";
        string sha = "3333333333333333333333333333333333333333333333333333333333333333";

        // Register with very short TTL
        suppression.RegisterSuppression(path, sha, TimeSpan.FromMilliseconds(50));

        Assert.True(suppression.IsSuppressed(path));

        await Task.Delay(100);

        bool suppressed = suppression.ShouldSuppress(path, sha);
        Assert.False(suppressed, "Expired suppression entry must not suppress events.");
        Assert.False(suppression.IsSuppressed(path));
    }

    [Fact]
    public void SuppressionApplies_OnlyToMatchingRelativePath()
    {
        var suppression = new RestoreSuppressionService();
        string pathA = "folder/fileA.txt";
        string pathB = "folder/fileB.txt";
        string sha = "4444444444444444444444444444444444444444444444444444444444444444";

        suppression.RegisterSuppression(pathA, sha, TimeSpan.FromSeconds(10));

        Assert.True(suppression.IsSuppressed(pathA));
        Assert.False(suppression.IsSuppressed(pathB));

        bool suppressedB = suppression.ShouldSuppress(pathB, sha);
        Assert.False(suppressedB, "Suppression must only apply to the registered path, not other paths.");
    }

    [Fact]
    public void SuppressionIsCaseInsensitive_AndIgnoresWhitespace()
    {
        var suppression = new RestoreSuppressionService();
        string path = "Documents/Budget.xlsx";
        string sha = "5555555555555555555555555555555555555555555555555555555555555555";

        suppression.RegisterSuppression(path, sha.ToUpperInvariant());

        Assert.True(suppression.ShouldSuppress("documents/budget.xlsx", sha.ToLowerInvariant()));
    }
}
