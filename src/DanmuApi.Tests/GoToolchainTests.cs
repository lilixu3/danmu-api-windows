using System.Diagnostics;
using System.Text;

namespace DanmuApi.Tests;

public sealed class GoToolchainTests
{
    [Fact]
    public void AllGoCallsPinRootAndDisableUserConfigAndBuildRecordsCompleteToolProvenance()
    {
        var root = RepositoryRoot();
        var acquire = File.ReadAllText(Path.Combine(root, "build", "Get-GoToolchain.ps1"));
        var build = File.ReadAllText(Path.Combine(root, "build", "Build-OutboundHelper.ps1"));
        Assert.Contains("$env:GOROOT=$installRoot; $env:GOENV='off'", acquire);
        Assert.Contains("$env:GOROOT=$tool.Root; $env:GOENV='off'", build);
        Assert.Contains("$entry.Open()", acquire);
        Assert.Contains("Installed Go archive input checksum mismatch", acquire);
        Assert.Contains("Get-ChildItem -LiteralPath $pending.Dequeue() -Force", acquire);
        Assert.Contains("Unlisted installed Go directory", acquire);
        Assert.Contains("GOTOOLDIR", acquire);
        Assert.Contains("finally { foreach ($name in $variables)", acquire);
        Assert.Contains("foreach ($name in $variables)", build);
        Assert.Contains("[string]$OutputRoot", build);
        Assert.Contains("Outbound output exists; use a fresh OutputRoot", build);
        Assert.DoesNotContain("Remove-Item -LiteralPath $zipPath", build);
        Assert.Contains("productionSourceInputs=$productionSourceInputs", build);
        Assert.Contains("Recorded source changed during native build", build);
        foreach (var field in new[] { "toolchainInputSha256", "toolchainDriverSha256", "toolchainCompilerSha256", "toolchainLinkerSha256", "toolchainStandardLibrarySha256" })
            Assert.Contains(field, build);
        foreach (var name in new[] { "Get-GoToolchain.ps1", "Build-OutboundHelper.ps1", "New-OutboundRuntimeBundle.ps1", "Build-WindowsRelease.ps1" })
            Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, File.ReadAllBytes(Path.Combine(root, "build", name))[..3]);
    }

    [SkippableFact]
    public async Task OfficialCachedTreeRejectsCompilerLinkerStandardInputAndHiddenExtrasAndRestoresEnvironment()
    {
        var tools = Environment.GetEnvironmentVariable("DANMU_TEST_GO_TOOLS_ROOT");
        var downloads = Environment.GetEnvironmentVariable("DANMU_TEST_GO_DOWNLOAD_ROOT");
        Skip.If(string.IsNullOrEmpty(tools) || string.IsNullOrEmpty(downloads), "Explicit official cached Go roots required; this test never downloads a toolchain.");
        var official = Path.Combine(tools!, "go1.26.0");
        var archive = Path.Combine(downloads!, "go1.26.0.windows-amd64.zip");
        Assert.True(File.Exists(Path.Combine(official, "toolchain.json")) && File.Exists(archive));
        using var temporary = new TemporaryDirectory();
        var script = Path.Combine(temporary.Path, "toolchain-check.ps1");
        var acquire = Path.Combine(RepositoryRoot(), "build", "Get-GoToolchain.ps1");
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var body = $$"""
            $ErrorActionPreference='Stop'
            $script={{Quote(acquire)}}; $tools={{Quote(tools!)}}; $downloads={{Quote(downloads!)}}
            $scratch={{Quote(temporary.Path)}}
            $env:GOROOT='C:\hostile-root';$env:GOENV='C:\hostile-goenv';$env:GOTOOLCHAIN='hostile';$env:GOFLAGS='-invalid-flag'
            function Assert-Restored {
              if($env:GOROOT -cne 'C:\hostile-root' -or $env:GOENV -cne 'C:\hostile-goenv' -or $env:GOTOOLCHAIN -cne 'hostile' -or $env:GOFLAGS -cne '-invalid-flag'){throw 'Environment not restored'}
            }
            $tool=& $script -ToolsRoot $tools -DownloadRoot $downloads
            Assert-Restored
            if($tool.InputCount -le 1000 -or $tool.CompilerSha256 -cnotmatch '^[0-9a-f]{64}$' -or $tool.LinkerSha256 -cnotmatch '^[0-9a-f]{64}$' -or $tool.StandardLibrarySha256 -cnotmatch '^[0-9a-f]{64}$'){throw 'Missing complete provenance'}
            $isolated=Join-Path $scratch 'tools';New-Item -ItemType Directory $isolated | Out-Null
            Copy-Item -LiteralPath (Join-Path $tools 'go1.26.0') -Destination $isolated -Recurse -Force
            foreach($relative in 'pkg\tool\windows_amd64\compile.exe','pkg\tool\windows_amd64\link.exe','src\runtime\runtime2.go'){
              $path=Join-Path (Join-Path $isolated 'go1.26.0') $relative
              [IO.File]::WriteAllText($path,'damaged test input, never executed')
              $rejected=$false
              try{$null=& $script -ToolsRoot $isolated -DownloadRoot $downloads}catch{if($_.Exception.Message -notlike '*input checksum mismatch*'){throw};$rejected=$true}
              if(-not $rejected){throw ('Accepted tampered '+$relative)}
              Assert-Restored
              Copy-Item -LiteralPath (Join-Path (Join-Path $tools 'go1.26.0') $relative) -Destination $path -Force
            }
            $extra=Join-Path $isolated 'go1.26.0\foreign-empty'
            New-Item -ItemType Directory $extra | Out-Null
            (Get-Item -LiteralPath $extra).Attributes=[IO.FileAttributes]::Directory -bor [IO.FileAttributes]::Hidden
            $rejected=$false
            try{$null=& $script -ToolsRoot $isolated -DownloadRoot $downloads}catch{if($_.Exception.Message -notlike '*Unlisted installed Go directory*'){throw};$rejected=$true}
            if(-not $rejected){throw 'Accepted hidden unlisted directory'}
            Assert-Restored
            Write-Output 'Official Go provenance and environment regression passed'
            """;
        File.WriteAllText(script, body, new UTF8Encoding(true));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, output);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "build", "Get-GoToolchain.ps1"))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
