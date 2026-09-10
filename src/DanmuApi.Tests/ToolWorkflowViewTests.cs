using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using DanmuApi.App.Controls;
using DanmuApi.App.Views;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class ToolWorkflowViewTests
{
    [AvaloniaFact]
    public void DanmuDetailsViewContainsHeatmapSourceExportAndEmptyFilterControls()
    {
        var view = new DanmuDetailsView();

        Assert.NotNull(view.FindControl<DanmuHeatmapControl>("HeatmapTimeline"));
        Assert.NotNull(view.FindControl<Button>("CopySourceButton"));
        Assert.NotNull(view.FindControl<Button>("OpenSourceButton"));
        Assert.NotNull(view.FindControl<ComboBox>("ExportFormatComboBox"));
        Assert.NotNull(view.FindControl<Button>("ExportButton"));
        Assert.NotNull(view.FindControl<Border>("EmptyFilterState"));
    }

    [AvaloniaFact]
    public void ApiDebugViewContainsDynamicBodyCancelAndCurlControls()
    {
        var view = new ApiDebugView();

        Assert.NotNull(view.FindControl<ItemsControl>("DynamicParameters"));
        Assert.NotNull(view.FindControl<StackPanel>("RawBodyPanel"));
        Assert.NotNull(view.FindControl<Button>("CancelRequestButton"));
        Assert.NotNull(view.FindControl<Button>("ExecuteRequestButton"));
        Assert.NotNull(view.FindControl<Button>("CopyCurlButton"));
    }

    [AvaloniaFact]
    public void DanmuDownloadViewContainsAllSectionsAndControls()
    {
        var view = new DanmuDownloadView();

        Assert.NotNull(view.FindControl<ListBox>("SectionList"));
        Assert.NotNull(view.FindControl<ScrollViewer>("SearchPanel"));
        Assert.NotNull(view.FindControl<ScrollViewer>("QueuePanel"));
        Assert.NotNull(view.FindControl<ScrollViewer>("RecordsPanel"));
        Assert.NotNull(view.FindControl<ScrollViewer>("SettingsPanel"));
        Assert.NotNull(view.FindControl<StackPanel>("AnimeListStage"));
        Assert.NotNull(view.FindControl<StackPanel>("EpisodeStage"));
        Assert.NotNull(view.FindControl<StackPanel>("RecordAnimeStage"));
        Assert.NotNull(view.FindControl<StackPanel>("RecordEpisodeStage"));
    }

    [AvaloniaFact]
    public void SearchResultsAndRequestRecordsExposeRealActions()
    {
        var search = new SearchResultsView();
        var records = new RequestRecordsView();

        Assert.NotNull(search.FindControl<Button>("FavoriteSnapshotButton"));
        Assert.NotNull(records.FindControl<Button>("ClearRecordsButton"));
        Assert.NotNull(records.FindControl<Button>("RefreshRecordsButton"));
        Assert.NotNull(records.FindControl<TextBox>("RecordSearchBox"));
        Assert.NotNull(records.FindControl<ComboBox>("OutcomeFilter"));
        Assert.NotNull(records.FindControl<ComboBox>("MethodFilter"));
        Assert.NotNull(records.FindControl<ComboBox>("TimeRangeFilter"));
    }

    [AvaloniaTheory]
    [InlineData(720)]
    [InlineData(960)]
    [InlineData(1280)]
    public void ToolsCategoriesKeepSettingsStyleSidebar(int width)
    {
        var view = new ToolsView();
        var window = new Window { Width = width, Height = 800, Content = view };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Null(view.FindControl<ComboBox>("CompactSectionSelector"));
            Assert.True(view.FindControl<Border>("SectionSidebar")!.IsVisible);
            Assert.Equal(208, view.FindControl<Border>("SectionSidebar")!.Bounds.Width);
            Assert.Equal(1, Grid.GetColumn(view.FindControl<ContentControl>("ToolContent")!));
            Assert.True(view.FindControl<ContentControl>("ToolContent")!.Bounds.Width > 0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HeatmapSelectsBucketsByRatioAndKeyboard()
    {
        var control = new DanmuHeatmapControl
        {
            Buckets = Enumerable.Range(0, 20)
                .Select(index => new DanmuHeatmapBucket(index, index * 30, (index + 1) * 30, index, index / 19d))
                .ToArray(),
        };

        control.SelectAtRatio(0.5);
        Assert.Equal(10, control.SelectedIndex);
        Assert.True(control.SelectByKey(Key.Left));
        Assert.Equal(9, control.SelectedIndex);
        Assert.True(control.SelectByKey(Key.End));
        Assert.Equal(19, control.SelectedIndex);
        Assert.True(control.SelectByKey(Key.Home));
        Assert.Equal(0, control.SelectedIndex);
        Assert.False(control.SelectByKey(Key.Enter));
        Assert.Equal(0, control.SelectedIndex);
    }

    [Fact]
    public void HeatmapRangeUsesHoursForLongVideos()
    {
        var bucket = new DanmuHeatmapBucket(0, 3661, 3691, 2, 1);

        Assert.Equal("01:01:01 - 01:01:31", bucket.RangeText);
    }
}
