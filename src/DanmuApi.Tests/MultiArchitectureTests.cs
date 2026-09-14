using System.Runtime.InteropServices;
using DanmuApi.Core;
using DanmuApi.Core.ApplicationUpdates;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

/// <summary>Covers the multi-architecture plumbing: release asset names, the signed manifest name per
/// architecture, and the native system directory a 32-bit build must use on 64-bit Windows.</summary>
public sealed class MultiArchitectureTests
{
    [Fact]
    public void ProcessArchitecturesMapToReleaseAssetNames()
    {
        Assert.Equal("win-x64", ApplicationArchitecture.Name(Architecture.X64));
        Assert.Equal("win-x86", ApplicationArchitecture.Name(Architecture.X86));
        Assert.Equal("win-arm64", ApplicationArchitecture.Name(Architecture.Arm64));
        Assert.Throws<InvalidOperationException>(() => ApplicationArchitecture.Name(Architecture.Arm));
        Assert.StartsWith("win-", ApplicationArchitecture.Current, StringComparison.Ordinal);
    }

    /// <summary>Released x64 clients look the manifest up by a fixed asset name, so x64 must keep the
    /// historical unsuffixed name; the other architectures get a suffixed one in the same release.</summary>
    [Fact]
    public void X64KeepsTheHistoricalManifestAssetNames()
    {
        if (ApplicationArchitecture.Current == "win-x64")
        {
            Assert.Equal("update-manifest.json", ApplicationUpdateService.ManifestFileName);
            Assert.Equal("update-manifest.json.sig", ApplicationUpdateService.SignatureFileName);
        }
        else
        {
            Assert.Equal($"update-manifest-{ApplicationArchitecture.Current}.json", ApplicationUpdateService.ManifestFileName);
            Assert.Equal(ApplicationUpdateService.ManifestFileName + ".sig", ApplicationUpdateService.SignatureFileName);
        }
    }

    [Theory]
    [InlineData(true, false, "Sysnative")]
    [InlineData(true, true, "System32")]
    [InlineData(false, false, "System32")]
    public void NativeSystemDirectoryAvoidsWow64Redirection(bool is64BitOperatingSystem, bool is64BitProcess, string expected)
    {
        Assert.Equal(expected, SystemPaths.NativeSystemDirectoryName(is64BitOperatingSystem, is64BitProcess));
    }

    [Fact]
    public void NativeSystemDirectoryCombinesSystemRootWithTheExpectedName()
    {
        var expected = SystemPaths.NativeSystemDirectoryName(Environment.Is64BitOperatingSystem, Environment.Is64BitProcess);
        Assert.Equal(Path.Combine(@"C:\Windows", expected), SystemPaths.NativeSystemDirectory(@"C:\Windows"));
        Assert.Throws<ArgumentException>(() => SystemPaths.NativeSystemDirectory("   "));
    }
}
