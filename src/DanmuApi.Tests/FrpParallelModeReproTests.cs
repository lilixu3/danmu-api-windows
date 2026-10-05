using Avalonia.Headless.XUnit;
using DanmuApi.App.ViewModels;

namespace DanmuApi.Tests;

public sealed class FrpParallelModeReproTests
{
    [AvaloniaFact]
    public async Task TextTabCanSaveWithoutFillingOrImportingIntoTheVisualForm()
    {
        using var harness = FrpTestHarness.Create();
        await using var config = new FrpTunnelConfigViewModel(harness.Service,
            new RecordingDialogService(), harness.Diagnostics, () => 9321);
        await config.ReloadCommand.ExecuteAsync(null);
        config.SelectedSectionOption = config.SectionOptions.Single(option => option.Value == FrpConfigSection.Text);
        config.ConfigText = """
            {"serverAddr":"frp.example.com","serverPort":7000,
             "proxies":[{"name":"raw-api","type":"tcp","localIP":"127.0.0.1","localPort":9321,"remotePort":19321}]}
            """;

        await config.SaveCommand.ExecuteAsync(null);

        Assert.Contains("已保存", config.OperationMessage, StringComparison.Ordinal);
        Assert.Equal("", config.ServerAddress);
        Assert.Equal(DanmuApi.Core.Frp.FrpConfigMode.Text, harness.Store.Read(9321).Settings.ConfigMode);
        Assert.Equal(config.ConfigText, harness.Store.Read(9321).Settings.RawConfig);
    }
}
