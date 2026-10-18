using System.Net;
using System.Text.RegularExpressions;
using Backup.Client.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Backup.Tests;

public class BlazorStaticAssetsRegressionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BlazorStaticAssetsRegressionTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RootPage_Returns200_AndReferencesValidCssBundles()
    {
        using var client = _factory.CreateClient();

        // 1. GET / -> 200
        var rootResponse = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);

        string html = await rootResponse.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(html));
        Assert.Contains("blazor.web.js", html);

        // 2. Discover all <link rel="stylesheet" href="...">
        var matches = Regex.Matches(html, @"<link[^>]*rel=""stylesheet""[^>]*href=""([^""]+)""", RegexOptions.IgnoreCase);
        Assert.NotEmpty(matches);

        var cssHrefs = matches.Select(m => m.Groups[1].Value).ToList();

        // Must reference app*.css
        var appCssHref = cssHrefs.FirstOrDefault(h => h.Contains("app.") || h.EndsWith("app.css"));
        Assert.NotNull(appCssHref);

        // Must reference Backup.Client.Web*.styles.css
        var scopedCssHref = cssHrefs.FirstOrDefault(h => h.Contains("Backup.Client.Web") && h.Contains("styles.css"));
        Assert.NotNull(scopedCssHref);

        // 3. Verify each discovered stylesheet
        foreach (var href in cssHrefs)
        {
            string url = "/" + href.TrimStart('/');
            var cssResponse = await client.GetAsync(url);

            Assert.True(
                cssResponse.StatusCode == HttpStatusCode.OK,
                $"Expected 200 OK for stylesheet '{href}', but got {cssResponse.StatusCode}");

            Assert.NotNull(cssResponse.Content.Headers.ContentType);
            Assert.Equal("text/css", cssResponse.Content.Headers.ContentType.MediaType);

            string cssContent = await cssResponse.Content.ReadAsStringAsync();
            Assert.True(cssContent.Length > 0, $"Stylesheet '{href}' content must not be empty.");
            Assert.DoesNotContain("<!DOCTYPE html>", cssContent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("An unhandled exception", cssContent, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ScopedCssBundle_ContainsIsolatedStyles_AndIsNever500()
    {
        using var client = _factory.CreateClient();

        var rootResponse = await client.GetAsync("/");
        string html = await rootResponse.Content.ReadAsStringAsync();

        var match = Regex.Match(html, @"href=""([^""]*Backup\.Client\.Web[^""]*styles\.css)""", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Could not find Backup.Client.Web styles bundle href in root HTML.");

        string bundleHref = "/" + match.Groups[1].Value.TrimStart('/');
        var bundleResponse = await client.GetAsync(bundleHref);

        Assert.Equal(HttpStatusCode.OK, bundleResponse.StatusCode);
        Assert.Equal("text/css", bundleResponse.Content.Headers.ContentType?.MediaType);

        string content = await bundleResponse.Content.ReadAsStringAsync();
        Assert.True(content.Length > 0);
        Assert.Contains("blazor-error-ui", content);
        Assert.DoesNotContain("Internal Server Error", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LivePort5100_IfRunning_ReturnsAllCssWith200()
    {
        // When running in live test or end-to-end environment where port 5100 is listening
        using var handler = new HttpClientHandler();
        using var liveClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

        HttpResponseMessage rootResponse;
        try
        {
            rootResponse = await liveClient.GetAsync("http://127.0.0.1:5100/");
        }
        catch
        {
            // If live standalone cluster is not currently running on 5100, skip live check
            return;
        }

        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
        string html = await rootResponse.Content.ReadAsStringAsync();

        var matches = Regex.Matches(html, @"<link[^>]*rel=""stylesheet""[^>]*href=""([^""]+)""", RegexOptions.IgnoreCase);
        Assert.NotEmpty(matches);

        foreach (Match match in matches)
        {
            string href = match.Groups[1].Value;
            string url = "http://127.0.0.1:5100/" + href.TrimStart('/');
            var cssResp = await liveClient.GetAsync(url);

            Assert.Equal(HttpStatusCode.OK, cssResp.StatusCode);
            Assert.Equal("text/css", cssResp.Content.Headers.ContentType?.MediaType);
            string body = await cssResp.Content.ReadAsStringAsync();
            Assert.True(body.Length > 0);
            Assert.DoesNotContain("<!DOCTYPE html>", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/files")]
    [InlineData("/history")]
    public async Task Pages_RenderWithoutLiteralRazorExpressions(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("v@", html);
        Assert.DoesNotContain("@file", html);
        Assert.DoesNotContain("@ver", html);
        Assert.DoesNotContain("@lastRestore", html);
    }

    [Fact]
    public async Task Live_PopulateSmokeFilesAndVerifyUiDetailsPage()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

        using var handler = new HttpClientHandler();
        using var liveClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var ping = await liveClient.GetAsync("http://127.0.0.1:5100/");
            if (!ping.IsSuccessStatusCode) return;
        }
        catch { return; }

        // Detect ClientDataRoot used by the live client on port 5100 (supports shared-demo and standard mode)
        string clientDataDir = Path.Combine(repoRoot, "data", "client");
        if (File.Exists(Path.Combine(repoRoot, "data", "client-a", "sync-profile.json")))
        {
            clientDataDir = Path.Combine(repoRoot, "data", "client-a");
        }

        var profileService = new SyncProfileService(clientDataDir);
        var activeProfile = await profileService.GetProfileAsync();
        if (activeProfile == null && clientDataDir != Path.Combine(repoRoot, "data", "client"))
        {
            clientDataDir = Path.Combine(repoRoot, "data", "client");
            profileService = new SyncProfileService(clientDataDir);
            activeProfile = await profileService.GetProfileAsync();
        }

        string clientStatePath = Path.Combine(clientDataDir, "client-state.json");
        var identityService = new ClientIdentityService(clientStatePath);
        var gatewayClient = new BackupGatewayClient("127.0.0.1", 5000);
        var clientService = new BackupClientService(gatewayClient, identityService);

        await clientService.EnsureRegisteredAsync();

        Guid fileId;
        if (activeProfile != null)
        {
            var filesResp = await clientService.GetFilesAsync(activeProfile.BackupSetId, includeDeleted: true);
            var candidate = filesResp.Success && filesResp.Files != null
                ? filesResp.Files.FirstOrDefault(f => f.CurrentVersionNumber >= 2)
                : null;

            if (candidate != null)
            {
                fileId = candidate.FileId;
            }
            else
            {
                string localRoot = activeProfile.LocalRootPath;
                Directory.CreateDirectory(localRoot);
                string testFilePath = Path.Combine(localRoot, "smoke_ui_test_file.txt");

                await File.WriteAllTextAsync(testFilePath, "Smoke Details Version 1");
                var up1 = await clientService.BackupSingleFileAsync(activeProfile.BackupSetId, localRoot, testFilePath);
                Assert.True(up1.Success, $"Upload v1 failed: {up1.ErrorMessage}");

                await File.WriteAllTextAsync(testFilePath, "Smoke Details Version 2 (Modified)");
                var up2 = await clientService.BackupSingleFileAsync(activeProfile.BackupSetId, localRoot, testFilePath);
                Assert.True(up2.Success, $"Upload v2 failed: {up2.ErrorMessage}");

                fileId = up1.FileId!.Value;
            }
        }
        else
        {
            string localRoot = Path.Combine(repoRoot, "data", "smoke_ui_test");
            Directory.CreateDirectory(localRoot);
            string testFilePath = Path.Combine(localRoot, "notes.txt");

            var bs = await clientService.CreateBackupSetAsync("DemoProjectSet", localRoot);
            if (!bs.Success) return;

            await profileService.SaveProfileAsync(new SyncProfile
            {
                BackupSetId = bs.BackupSetId,
                Name = "DemoProjectSet",
                LocalRootPath = localRoot,
                AutomaticSyncEnabled = false
            });

            await File.WriteAllTextAsync(testFilePath, "Content Version 1");
            var up1 = await clientService.BackupSingleFileAsync(bs.BackupSetId, localRoot, testFilePath);
            Assert.True(up1.Success, $"Upload v1 failed: {up1.ErrorMessage}");

            await File.WriteAllTextAsync(testFilePath, "Content Version 2 (Modified)");
            var up2 = await clientService.BackupSingleFileAsync(bs.BackupSetId, localRoot, testFilePath);
            Assert.True(up2.Success, $"Upload v2 failed: {up2.ErrorMessage}");

            fileId = up1.FileId!.Value;
        }

        // 1. Verify backend state directly via API/service
        var verResp = await clientService.GetVersionsAsync(fileId);
        Assert.True(verResp.Success, $"GetVersionsAsync failed: {verResp.ErrorMessage}");
        Assert.NotNull(verResp.Versions);
        Assert.True(verResp.Versions.Count >= 2, $"Expected >= 2 versions, found {verResp.Versions.Count}");
        Assert.Contains(verResp.Versions, v => v.VersionNumber == 1);
        Assert.Contains(verResp.Versions, v => v.VersionNumber == 2);

        // 2. Verify Blazor Web UI FileDetails page renders properly with version history
        var fileDetailsResp = await liveClient.GetAsync($"http://127.0.0.1:5100/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, fileDetailsResp.StatusCode);
        string detailsHtml = await fileDetailsResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("v@", detailsHtml);
        Assert.DoesNotContain("alert alert-danger", detailsHtml);
        Assert.Contains("v2", detailsHtml);
        Assert.Contains("v1", detailsHtml);
    }
}
