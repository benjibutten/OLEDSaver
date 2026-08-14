using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OLEDSaver.Updates;
using Xunit;

namespace OLEDSaver.Tests;

public sealed class GitHubUpdateServiceTests
{
    [Theory]
    [InlineData("v2026.8.12", 2026, 8, 12)]
    [InlineData("2025.11.3", 2025, 11, 3)]
    public void TryParseVersion_ParsesReleaseTags(string tag, int major, int minor, int build)
    {
        Assert.True(GitHubUpdateService.TryParseVersion(tag, out Version? version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void GetReleaseZipName_MatchesTheNameTheReleaseWorkflowUploads()
    {
        string result = GitHubUpdateService.GetReleaseZipName(new Version(2026, 8, 12));

        Assert.Equal("OLEDSaver-2026.8.12-win-x64.zip", result);
    }

    [Fact]
    public void ParseChecksum_AcceptsStandardSha256File()
    {
        string hash = new('a', 64);

        string result = GitHubUpdateService.ParseChecksum($"{hash}  OLEDSaver-2026.8.12-win-x64.zip");

        Assert.Equal(hash.ToUpperInvariant(), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaz")]
    public void ParseChecksum_RejectsInvalidContent(string value)
    {
        Assert.Throws<InvalidDataException>(() => GitHubUpdateService.ParseChecksum(value));
    }

    [Fact]
    public async Task CheckAsync_UsesLatestReleaseAndSelectsMatchingAssets()
    {
        const string zipUrl = "https://github.com/benjibutten/OLEDSaver/releases/download/v2026.8.12/OLEDSaver-2026.8.12-win-x64.zip";
        const string checksumUrl = $"{zipUrl}.sha256";
        string? requestedUrl = null;
        string json = $$"""
            {
              "tag_name": "v2026.8.12",
              "html_url": "https://github.com/benjibutten/OLEDSaver/releases/tag/v2026.8.12",
              "assets": [
                { "name": "OLEDSaver-2026.8.11-win-x64.zip", "browser_download_url": "https://example.test/wrong.zip" },
                { "name": "OLEDSaver-2026.8.12-win-x64.zip", "browser_download_url": "{{zipUrl}}" },
                { "name": "OLEDSaver-2026.8.12-win-x64.zip.sha256", "browser_download_url": "{{checksumUrl}}" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestedUrl = request.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }));
        string stateRoot = Path.Combine(Path.GetTempPath(), $"OLEDSaver-update-check-test-{Guid.NewGuid():N}");

        try
        {
            var service = new GitHubUpdateService(client, Path.Combine(stateRoot, "state.txt"));
            UpdateInfo? update = await service.CheckAsync(new Version(2026, 8, 11), force: true);

            Assert.Equal("https://api.github.com/repos/benjibutten/OLEDSaver/releases/latest", requestedUrl);
            Assert.NotNull(update);
            Assert.Equal(new Uri(zipUrl), update!.DownloadUri);
            Assert.Equal(new Uri(checksumUrl), update.ChecksumUri);
        }
        finally
        {
            if (Directory.Exists(stateRoot))
                Directory.Delete(stateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_IgnoresReleaseThatIsNotNewerThanTheInstalledBuild()
    {
        string json = """
            {
              "tag_name": "v2026.8.12",
              "html_url": "https://github.com/benjibutten/OLEDSaver/releases/tag/v2026.8.12",
              "assets": [
                { "name": "OLEDSaver-2026.8.12-win-x64.zip", "browser_download_url": "https://example.test/app.zip" },
                { "name": "OLEDSaver-2026.8.12-win-x64.zip.sha256", "browser_download_url": "https://example.test/app.zip.sha256" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        string stateRoot = Path.Combine(Path.GetTempPath(), $"OLEDSaver-update-check-test-{Guid.NewGuid():N}");

        try
        {
            var service = new GitHubUpdateService(client, Path.Combine(stateRoot, "state.txt"));

            Assert.Null(await service.CheckAsync(new Version(2026, 8, 12), force: true));
        }
        finally
        {
            if (Directory.Exists(stateRoot))
                Directory.Delete(stateRoot, recursive: true);
        }
    }

    /// <summary>
    /// The one case where offering an update would be worse than staying quiet: a
    /// release the app has no verified way to install.
    /// </summary>
    [Fact]
    public async Task CheckAsync_IgnoresReleaseWithoutChecksumAsset()
    {
        string json = """
            {
              "tag_name": "v2026.8.12",
              "html_url": "https://github.com/benjibutten/OLEDSaver/releases/tag/v2026.8.12",
              "assets": [
                { "name": "OLEDSaver-2026.8.12-win-x64.zip", "browser_download_url": "https://example.test/app.zip" }
              ]
            }
            """;
        using var client = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        string stateRoot = Path.Combine(Path.GetTempPath(), $"OLEDSaver-update-check-test-{Guid.NewGuid():N}");

        try
        {
            var service = new GitHubUpdateService(client, Path.Combine(stateRoot, "state.txt"));

            Assert.Null(await service.CheckAsync(new Version(2026, 8, 11), force: true));
        }
        finally
        {
            if (Directory.Exists(stateRoot))
                Directory.Delete(stateRoot, recursive: true);
        }
    }

    /// <summary>
    /// Without the interval the app would ask GitHub on every launch, which for a tray
    /// app that starts at logon is a request per reboot for no new information.
    /// </summary>
    [Fact]
    public async Task CheckAsync_SkipsTheNetworkWhenAnAutomaticCheckIsNotDueYet()
    {
        int requestCount = 0;
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "tag_name": "v2026.8.12", "html_url": "https://example.test", "assets": [] }""",
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        string stateRoot = Path.Combine(Path.GetTempPath(), $"OLEDSaver-update-check-test-{Guid.NewGuid():N}");

        try
        {
            var service = new GitHubUpdateService(client, Path.Combine(stateRoot, "state.txt"));

            await service.CheckAsync(new Version(2026, 8, 11), force: true);
            await service.CheckAsync(new Version(2026, 8, 11), force: false);

            Assert.Equal(1, requestCount);
        }
        finally
        {
            if (Directory.Exists(stateRoot))
                Directory.Delete(stateRoot, recursive: true);
        }
    }

    [Fact]
    public async Task VerifySha256Async_AcceptsMatchAndRejectsMismatch()
    {
        byte[] download = Encoding.UTF8.GetBytes("OLED Saver release archive");
        string expectedHash = Convert.ToHexString(SHA256.HashData(download));

        await GitHubUpdateService.VerifySha256Async(new MemoryStream(download), expectedHash);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GitHubUpdateService.VerifySha256Async(new MemoryStream(download), new string('0', 64)));
    }

    [Fact]
    public void InstallFiles_UpdatesReleaseFilesAndPreservesOtherFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"OLEDSaver-update-test-{Guid.NewGuid():N}");
        string staging = Path.Combine(root, "staging");
        string install = Path.Combine(root, "install");
        string backup = Path.Combine(root, "backup");

        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(staging, "OLEDSaver.exe"), "new version");
            File.WriteAllText(Path.Combine(staging, "LICENSE.txt"), "new licence");
            File.WriteAllText(Path.Combine(install, "OLEDSaver.exe"), "old version");
            File.WriteAllText(Path.Combine(install, "user-file.txt"), "keep me");

            UpdateInstaller.InstallFiles(staging, install, backup);

            Assert.Equal("new version", File.ReadAllText(Path.Combine(install, "OLEDSaver.exe")));
            Assert.Equal("new licence", File.ReadAllText(Path.Combine(install, "LICENSE.txt")));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(install, "user-file.txt")));
            Assert.Equal("old version", File.ReadAllText(Path.Combine(backup, "OLEDSaver.exe")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The staged tree here cannot be installed: "data" is a file in the install
    /// folder and a directory in the update, so creating it throws partway through.
    /// What matters is what the install looks like afterwards.
    /// </summary>
    [Fact]
    public void InstallFiles_PutsThePreviousVersionBackWhenTheInstallFailsPartway()
    {
        string root = Path.Combine(Path.GetTempPath(), $"OLEDSaver-update-test-{Guid.NewGuid():N}");
        string staging = Path.Combine(root, "staging");
        string install = Path.Combine(root, "install");
        string backup = Path.Combine(root, "backup");

        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(Path.Combine(staging, "data"));
            File.WriteAllText(Path.Combine(staging, "OLEDSaver.exe"), "new version");
            File.WriteAllText(Path.Combine(staging, "data", "blocked.txt"), "cannot land");
            File.WriteAllText(Path.Combine(install, "OLEDSaver.exe"), "old version");
            File.WriteAllText(Path.Combine(install, "data"), "a file, not a folder");

            Assert.ThrowsAny<Exception>(() => UpdateInstaller.InstallFiles(staging, install, backup));

            // The exe was replaced before the failure — the backup proves it — and then
            // put back, so the half-installed new version is gone rather than left next
            // to the old one.
            Assert.Equal("old version", File.ReadAllText(Path.Combine(backup, "OLEDSaver.exe")));
            Assert.Equal("old version", File.ReadAllText(Path.Combine(install, "OLEDSaver.exe")));
            Assert.Empty(Directory.GetFiles(install, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreBackups_ReportsNothingWhenEveryFileGoesBack()
    {
        string root = Path.Combine(Path.GetTempPath(), $"OLEDSaver-update-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);
            string replaced = Path.Combine(root, "OLEDSaver.exe");
            string backup = Path.Combine(root, "OLEDSaver.exe.backup");
            string added = Path.Combine(root, "added.txt");
            File.WriteAllText(replaced, "new version");
            File.WriteAllText(backup, "old version");
            File.WriteAllText(added, "did not exist before");

            IReadOnlyList<string> unrestored = UpdateInstaller.RestoreBackups(
            [
                new UpdateInstaller.InstalledFile(replaced, backup),
                new UpdateInstaller.InstalledFile(added, null)
            ]);

            Assert.Empty(unrestored);
            Assert.Equal("old version", File.ReadAllText(replaced));

            // A file the update added has no previous version, so putting things back
            // means removing it.
            Assert.False(File.Exists(added));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The case that decides whether the user can recover: if a file cannot be put
    /// back, the caller has to hear about it by name — silence there is what turns a
    /// failed update into a broken install with its backup deleted behind it.
    /// </summary>
    [Fact]
    public void RestoreBackups_ReportsTheFilesItCouldNotPutBack()
    {
        string root = Path.Combine(Path.GetTempPath(), $"OLEDSaver-update-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);
            string restorable = Path.Combine(root, "OLEDSaver.exe");
            string restorableBackup = Path.Combine(root, "OLEDSaver.exe.backup");
            string broken = Path.Combine(root, "OLEDSaver.dll");
            File.WriteAllText(restorable, "new version");
            File.WriteAllText(restorableBackup, "old version");
            File.WriteAllText(broken, "new version");

            IReadOnlyList<string> unrestored = UpdateInstaller.RestoreBackups(
            [
                new UpdateInstaller.InstalledFile(restorable, restorableBackup),
                new UpdateInstaller.InstalledFile(broken, Path.Combine(root, "gone.backup"))
            ]);

            Assert.Equal([broken], unrestored);

            // The one that could be restored still was: a file it cannot save is no
            // reason to abandon the rest.
            Assert.Equal("old version", File.ReadAllText(restorable));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A copy started at logon lives in the tray. Coming back from an update with the
    /// settings window open would put UI on screen that nobody asked for — possibly
    /// with nobody at the machine to close it.
    /// </summary>
    [Fact]
    public void CreateRestartStartInfo_KeepsTheAppInTheTrayWhenItWasStartedThere()
    {
        ProcessStartInfo restart = UpdateInstaller.CreateRestartStartInfo(
            @"C:\Apps\OLEDSaver\OLEDSaver.exe",
            @"C:\Apps\OLEDSaver",
            ["--apply-update", UpdateInstaller.RestartMinimizedArgument]);

        Assert.Contains(App.MinimizedArgument, restart.ArgumentList);
    }

    [Fact]
    public void CreateRestartStartInfo_OpensNormallyWhenTheAppWasNotStartedInTheTray()
    {
        ProcessStartInfo restart = UpdateInstaller.CreateRestartStartInfo(
            @"C:\Apps\OLEDSaver\OLEDSaver.exe",
            @"C:\Apps\OLEDSaver",
            ["--apply-update"]);

        Assert.Empty(restart.ArgumentList);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
