using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DanmuApi.App.Services;

namespace DanmuApi.Tests;

public sealed class ApplicationUpdateVersionIdentityTests
{
    [Theory]
    [InlineData("0.5.27", "0.5.27")]
    [InlineData("0.5.27+c1607deee183843555429312ed2ecce8883b776d", "0.5.27")]
    [InlineData("0.5.27+executable.1", "0.5.27+manifest.2")]
    [InlineData("0.5.27", "0.5.27+manifest.2")]
    [InlineData("0.5.27-preview.1+executable", "0.5.27-preview.1")]
    [InlineData("0.5.27-preview.1+executable", "0.5.27-preview.1+manifest")]
    public void MatchingReleaseIdentityIgnoresOnlyBuildMetadata(string productVersion, string expectedVersion) =>
        Assert.True(ApplicationUpdateHelper.VersionsMatch(productVersion, expectedVersion));

    [Theory]
    [InlineData("0.5.26+build", "0.5.27")]
    [InlineData("0.5.28+build", "0.5.27")]
    [InlineData("0.6.27+build", "0.5.27")]
    [InlineData("1.5.27+build", "0.5.27")]
    [InlineData("0.5.27-preview.1+build", "0.5.27")]
    [InlineData("0.5.27+build", "0.5.27-preview.1")]
    [InlineData("0.5.27-preview.2+build", "0.5.27-preview.1")]
    [InlineData("0.5.27-Preview.1+build", "0.5.27-preview.1")]
    public void DifferentVersionOrPrereleaseIsNeverAccepted(string productVersion, string expectedVersion) =>
        Assert.False(ApplicationUpdateHelper.VersionsMatch(productVersion, expectedVersion));

    [Theory]
    [InlineData("0.5.27.0", "0.5.27")]
    [InlineData("0.5.27", "0.5.27.0")]
    [InlineData("v0.5.27", "0.5.27")]
    [InlineData("0.5.27", "v0.5.27")]
    [InlineData("0.5.27+", "0.5.27")]
    [InlineData("0.5.27", "0.5.27+")]
    [InlineData("0.5.27-preview.01", "0.5.27")]
    [InlineData("0.5.27", "0.5.27-preview.01")]
    [InlineData("0.5.27\n", "0.5.27")]
    [InlineData("0.5.27", "0.5.27\n")]
    public void InvalidVersionOnEitherSideFailsExplicitly(string productVersion, string expectedVersion) =>
        Assert.Throws<FormatException>(() => ApplicationUpdateHelper.VersionsMatch(productVersion, expectedVersion));

    [Theory]
    [InlineData(null, "0.5.27")]
    [InlineData("0.5.27", null)]
    public void MissingVersionOnEitherSideFailsExplicitly(string? productVersion, string? expectedVersion) =>
        Assert.Throws<ArgumentNullException>(() => ApplicationUpdateHelper.VersionsMatch(productVersion!, expectedVersion!));

    [Theory]
    [InlineData("0.5.27+private-marker=secret", "0.5.27")]
    [InlineData("0.5.27", "0.5.27+private-marker=secret")]
    public void InvalidVersionDiagnosticsDoNotEchoInput(string productVersion, string expectedVersion)
    {
        var error = Assert.Throws<FormatException>(() => ApplicationUpdateHelper.VersionsMatch(productVersion, expectedVersion));
        Assert.DoesNotContain("private-marker", error.Message);
        Assert.DoesNotContain("private-marker", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(new FormatException().HResult, error.HResult);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RealExecutableMetadataProducesExactReceiptAndPreservesServiceResume(bool resumeService, bool expectedHasMetadata)
    {
        using var fixture = new ReceiptFixture(resumeService, expectedHasMetadata);
        Assert.Contains("+", fixture.ProductVersion);

        ApplicationUpdateHelper.AcknowledgeStartup(fixture.Args, fixture.TargetDirectory + Path.DirectorySeparatorChar, fixture.ExecutablePath);

        Assert.Equal(Encoding.UTF8.GetBytes(fixture.Job.ExpectedVersion), File.ReadAllBytes(fixture.ReceiptPath));
        Assert.False(File.Exists(fixture.ReceiptPath + ".tmp"));
        Assert.Equal(resumeService, ApplicationUpdateHelper.ShouldResumeService(fixture.Args, fixture.TargetDirectory, fixture.ExecutablePath));
        Assert.Equal(fixture.Job.ExpectedVersion, File.ReadAllText(fixture.ReceiptPath));
        Assert.Equal(fixture.OriginalJobBytes, File.ReadAllBytes(fixture.JobPath));
    }

    [Theory]
    [InlineData("9.8.7")]
    [InlineData("0.5.27-preview.1")]
    public void WrongExecutableVersionRejectsReceiptAndServiceResume(string expectedVersion)
    {
        using var fixture = new ReceiptFixture(true);
        fixture.SetJob(fixture.Job with { ExpectedVersion = expectedVersion });

        Assert.Throws<IOException>(() => ApplicationUpdateHelper.AcknowledgeStartup(fixture.Args, fixture.TargetDirectory, fixture.ExecutablePath));
        Assert.Throws<IOException>(() => ApplicationUpdateHelper.ShouldResumeService(fixture.Args, fixture.TargetDirectory, fixture.ExecutablePath));
        Assert.False(File.Exists(fixture.ReceiptPath));
        Assert.False(File.Exists(fixture.ReceiptPath + ".tmp"));
    }

    [Fact]
    public void WrongInstallationDirectoryRejectsReceiptAndServiceResume()
    {
        using var fixture = new ReceiptFixture(true);
        using var otherTarget = new TemporaryDirectory();
        fixture.SetJob(fixture.Job with { TargetDirectory = otherTarget.Path });

        Assert.Throws<IOException>(() => ApplicationUpdateHelper.AcknowledgeStartup(fixture.Args, fixture.TargetDirectory, fixture.ExecutablePath));
        Assert.Throws<IOException>(() => ApplicationUpdateHelper.ShouldResumeService(fixture.Args, fixture.TargetDirectory, fixture.ExecutablePath));
        Assert.False(File.Exists(fixture.ReceiptPath));
        Assert.False(File.Exists(fixture.ReceiptPath + ".tmp"));
    }

    [Fact]
    public void JobOutsideUserUpdateRootCannotPublishReceipt()
    {
        using var fixture = new ReceiptFixture(false);
        using var outside = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(outside.Path, "job.json"), fixture.OriginalJobBytes);
        string[] args = ["--app-update-receipt", outside.Path];

        Assert.Throws<IOException>(() => ApplicationUpdateHelper.AcknowledgeStartup(args, fixture.TargetDirectory, fixture.ExecutablePath));
        Assert.Throws<IOException>(() => ApplicationUpdateHelper.ShouldResumeService(args, fixture.TargetDirectory, fixture.ExecutablePath));
        Assert.False(File.Exists(Path.Combine(outside.Path, "startup.ok")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidJobFailsBeforePublishingReceipt(bool oversize)
    {
        using var fixture = new ReceiptFixture(false);
        File.WriteAllText(fixture.JobPath, oversize ? new string('x', 64 * 1024 + 1) : "{");

        if (oversize)
            Assert.Throws<IOException>(() => ApplicationUpdateHelper.AcknowledgeStartup(fixture.Args, fixture.TargetDirectory, fixture.ExecutablePath));
        else
            Assert.Throws<JsonException>(() => ApplicationUpdateHelper.AcknowledgeStartup(fixture.Args, fixture.TargetDirectory, fixture.ExecutablePath));
        Assert.False(File.Exists(fixture.ReceiptPath));
        Assert.False(File.Exists(fixture.ReceiptPath + ".tmp"));
    }

    [Fact]
    public void MalformedExpectedVersionFailsWithoutReceiptOrInputDisclosure()
    {
        using var fixture = new ReceiptFixture(false);
        fixture.SetJob(fixture.Job with { ExpectedVersion = "0.5.27+private-marker=secret" });

        var error = Assert.Throws<FormatException>(() => ApplicationUpdateHelper.AcknowledgeStartup(fixture.Args, fixture.TargetDirectory, fixture.ExecutablePath));
        Assert.DoesNotContain("private-marker", error.Message);
        Assert.DoesNotContain("private-marker", error.ToString());
        Assert.False(File.Exists(fixture.ReceiptPath));
        Assert.False(File.Exists(fixture.ReceiptPath + ".tmp"));
    }

    [Fact]
    public void OversizedExistingReceiptIsRejectedBeforeWriting()
    {
        using var fixture = new ReceiptFixture(false);
        var original = new string('x', 257);
        File.WriteAllText(fixture.ReceiptPath, original);

        Assert.Throws<IOException>(() => ApplicationUpdateHelper.AcknowledgeStartup(fixture.Args, fixture.TargetDirectory, fixture.ExecutablePath));
        Assert.Equal(original, File.ReadAllText(fixture.ReceiptPath));
        Assert.False(File.Exists(fixture.ReceiptPath + ".tmp"));
    }

    [Fact]
    public void ReceiptPublishFailureIsNotReportedAsSuccess()
    {
        using var fixture = new ReceiptFixture(false);
        fixture.SetJob(fixture.Job with { ExpectedVersion = fixture.ProductVersion });
        File.WriteAllText(fixture.ReceiptPath, "existing-receipt");
        using (var locked = new FileStream(fixture.ReceiptPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<UnauthorizedAccessException>(() => ApplicationUpdateHelper.AcknowledgeStartup(fixture.Args, fixture.TargetDirectory, fixture.ExecutablePath));
            Assert.Equal(5, error.HResult & 0xffff);
        }
        Assert.Equal("existing-receipt", File.ReadAllText(fixture.ReceiptPath));
        Assert.Equal(Encoding.UTF8.GetBytes(fixture.Job.ExpectedVersion), File.ReadAllBytes(fixture.ReceiptPath + ".tmp"));
    }

    private sealed class ReceiptFixture : IDisposable
    {
        private readonly TemporaryDirectory target = new();
        private readonly string directory;
        public string TargetDirectory => target.Path;
        public string ExecutablePath { get; }
        public string ProductVersion { get; }
        public string JobPath => Path.Combine(directory, "job.json");
        public string ReceiptPath => Path.Combine(directory, "startup.ok");
        public string[] Args => ["--app-update-receipt", directory];
        public ApplicationUpdateJob Job { get; private set; }
        public byte[] OriginalJobBytes { get; private set; } = [];

        public ReceiptFixture(bool resumeService, bool expectedHasMetadata = false)
        {
            directory = Path.Combine(ApplicationUpdateHelper.JobRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            ExecutablePath = Path.Combine(target.Path, "DanmuApi.App.exe");
            File.Copy(typeof(ApplicationUpdateHelper).Assembly.Location, ExecutablePath);
            ProductVersion = FileVersionInfo.GetVersionInfo(ExecutablePath).ProductVersion!;
            var expectedVersion = ProductVersion.Split('+')[0] + (expectedHasMetadata ? "+manifest.7" : "");
            Job = new(123, 456, target.Path, "installer.exe", "installer", expectedVersion, resumeService);
            SetJob(Job);
        }

        public void SetJob(ApplicationUpdateJob job)
        {
            Job = job;
            OriginalJobBytes = JsonSerializer.SerializeToUtf8Bytes(job);
            File.WriteAllBytes(JobPath, OriginalJobBytes);
        }

        public void Dispose()
        {
            Directory.Delete(directory, true);
            target.Dispose();
        }
    }
}
