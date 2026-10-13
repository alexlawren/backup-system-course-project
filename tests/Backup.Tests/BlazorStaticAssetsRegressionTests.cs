using System.Net;
using System.Text.RegularExpressions;
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
}
