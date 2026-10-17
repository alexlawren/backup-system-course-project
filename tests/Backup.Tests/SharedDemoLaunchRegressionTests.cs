using System.Net;
using System.Text.RegularExpressions;
using Backup.Client.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Backup.Tests;

public class SharedDemoLaunchRegressionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _baseFactory;

    public SharedDemoLaunchRegressionTests(WebApplicationFactory<Program> baseFactory)
    {
        _baseFactory = baseFactory;
    }

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "BackupSystem.sln")))
            {
                return current;
            }
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        throw new InvalidOperationException("Could not find repository root containing BackupSystem.sln");
    }

    [Fact]
    public void SharedDemoScripts_HaveUtf8BomAndNoSmartQuoteArtifacts()
    {
        string repoRoot = FindRepoRoot();
        string startScript = Path.Combine(repoRoot, "start-shared-demo.ps1");
        string stopScript = Path.Combine(repoRoot, "stop-shared-demo.ps1");

        Assert.True(File.Exists(startScript), $"start-shared-demo.ps1 must exist at {startScript}");
        Assert.True(File.Exists(stopScript), $"stop-shared-demo.ps1 must exist at {stopScript}");

        // 1. Must have UTF-8 BOM so Windows PowerShell 5.1 doesn't misinterpret Cyrillic as Windows-1252 smart quotes
        byte[] startBytes = File.ReadAllBytes(startScript);
        Assert.True(startBytes.Length >= 3, "start-shared-demo.ps1 cannot be empty");
        Assert.Equal(0xEF, startBytes[0]);
        Assert.Equal(0xBB, startBytes[1]);
        Assert.Equal(0xBF, startBytes[2]);

        byte[] stopBytes = File.ReadAllBytes(stopScript);
        Assert.True(stopBytes.Length >= 3, "stop-shared-demo.ps1 cannot be empty");
        Assert.Equal(0xEF, stopBytes[0]);
        Assert.Equal(0xBB, stopBytes[1]);
        Assert.Equal(0xBF, stopBytes[2]);

        // 2. No curly/smart quotes (U+201C, U+201D, U+2018, U+2019)
        string startText = File.ReadAllText(startScript);
        Assert.DoesNotContain("\u201C", startText);
        Assert.DoesNotContain("\u201D", startText);
        Assert.DoesNotContain("\u2018", startText);
        Assert.DoesNotContain("\u2019", startText);
    }

    [Fact]
    public void SharedDemoScript_ConfiguresDistinctParametersForClients()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "start-shared-demo.ps1");
        string content = File.ReadAllText(scriptPath);

        // Client A Port = 5100, Client B Port = 5200
        Assert.Contains("5100", content);
        Assert.Contains("5200", content);

        // URLs distinct
        Assert.Contains("http://127.0.0.1:5100", content);
        Assert.Contains("http://127.0.0.1:5200", content);

        // Machine names distinct
        Assert.Contains("DESKTOP-A", content);
        Assert.Contains("DESKTOP-B", content);

        // ClientDataRoots distinct
        Assert.Contains("data/client-a", content.Replace('\\', '/'));
        Assert.Contains("data/client-b", content.Replace('\\', '/'));

        // Argument escaping mechanism must exist to protect paths with spaces
        Assert.Contains("escapedArgs", content);
    }

    [Fact]
    public async Task TwoClients_CanRunWithIndependentIdentitiesAndDataRoots()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "backup_demo_test_" + Guid.NewGuid().ToString("N")[..8]);
        string dirA = Path.Combine(tempRoot, "data", "client-a");
        string dirB = Path.Combine(tempRoot, "data", "client-b");

        try
        {
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            using var factoryA = _baseFactory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ClientDataRoot", dirA);
                builder.UseSetting("MachineName", "DESKTOP-A");
                builder.UseSetting("ClientName", "Client-A");
            });

            using var factoryB = _baseFactory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ClientDataRoot", dirB);
                builder.UseSetting("MachineName", "DESKTOP-B");
                builder.UseSetting("ClientName", "Client-B");
            });

            using var clientA = factoryA.CreateClient();
            using var clientB = factoryB.CreateClient();

            // 1. Both endpoints return HTTP 200 OK
            var respA = await clientA.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, respA.StatusCode);

            var respB = await clientB.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, respB.StatusCode);

            string htmlA = await respA.Content.ReadAsStringAsync();
            string htmlB = await respB.Content.ReadAsStringAsync();

            // 2. Client A HTML displays DESKTOP-A and its own data root
            Assert.Contains("DESKTOP-A", htmlA);
            Assert.Contains("data/client-a", htmlA);
            Assert.DoesNotContain("DESKTOP-B", htmlA);

            // 3. Client B HTML displays DESKTOP-B and its own data root
            Assert.Contains("DESKTOP-B", htmlB);
            Assert.Contains("data/client-b", htmlB);
            Assert.DoesNotContain("DESKTOP-A", htmlB);

            // 4. Distinct ClientId and separate state files
            var identityA = factoryA.Services.GetRequiredService<IClientIdentityService>();
            var identityB = factoryB.Services.GetRequiredService<IClientIdentityService>();

            var guidA = Guid.NewGuid();
            var guidB = Guid.NewGuid();

            await identityA.SaveStateAsync(guidA, "DESKTOP-A", "Client-A");
            await identityB.SaveStateAsync(guidB, "DESKTOP-B", "Client-B");

            var stateA = await identityA.GetStateAsync();
            var stateB = await identityB.GetStateAsync();

            Assert.Equal(guidA, stateA.ClientId);
            Assert.Equal("DESKTOP-A", stateA.MachineName);
            Assert.Equal("Client-A", stateA.ClientName);

            Assert.Equal(guidB, stateB.ClientId);
            Assert.Equal("DESKTOP-B", stateB.MachineName);
            Assert.Equal("Client-B", stateB.ClientName);

            Assert.NotEqual(stateA.ClientId, stateB.ClientId);
            Assert.NotEqual(stateA.MachineName, stateB.MachineName);

            // State files written to separate directories
            Assert.True(File.Exists(Path.Combine(dirA, "client-state.json")), "State file A must exist in dirA");
            Assert.True(File.Exists(Path.Combine(dirB, "client-state.json")), "State file B must exist in dirB");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
            catch { }
        }
    }
}
