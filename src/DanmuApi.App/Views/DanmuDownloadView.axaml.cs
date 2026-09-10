using System.ComponentModel;
using Avalonia.Controls;
using DanmuApi.App.ViewModels;

namespace DanmuApi.App.Views;

public partial class DanmuDownloadView : UserControl
{
    public DanmuDownloadView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            if (DataContext is DanmuDownloadPageViewModel viewModel)
            {
                viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is DanmuDownloadPageViewModel viewModel)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateSectionVisibility(viewModel);
            UpdateSearchStageVisibility(viewModel);
            UpdateRecordStageVisibility(viewModel);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not DanmuDownloadPageViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(DanmuDownloadPageViewModel.SelectedSectionOption))
        {
            UpdateSectionVisibility(viewModel);
        }
        else if (e.PropertyName == nameof(DanmuDownloadPageViewModel.CurrentStage))
        {
            UpdateSearchStageVisibility(viewModel);
        }
        else if (e.PropertyName is nameof(DanmuDownloadPageViewModel.CurrentRecordStage)
            or nameof(DanmuDownloadPageViewModel.IsPreviewOpen))
        {
            UpdateRecordStageVisibility(viewModel);
        }
    }

    private void UpdateSectionVisibility(DanmuDownloadPageViewModel viewModel)
    {
        var section = viewModel.SelectedSectionOption.Value;
        SearchPanel.IsVisible = section == DanmuDownloadSection.Search;
        QueuePanel.IsVisible = section == DanmuDownloadSection.Queue;
        RecordsPanel.IsVisible = section == DanmuDownloadSection.Records;
        SettingsPanel.IsVisible = section == DanmuDownloadSection.Settings;
    }

    private void UpdateSearchStageVisibility(DanmuDownloadPageViewModel viewModel)
    {
        var stage = viewModel.CurrentStage;
        AnimeListStage.IsVisible = stage == DownloadSearchStage.AnimeList;
        EpisodeStage.IsVisible = stage == DownloadSearchStage.EpisodeList;
    }

    private void UpdateRecordStageVisibility(DanmuDownloadPageViewModel viewModel)
    {
        var previewOpen = viewModel.IsPreviewOpen;
        var stage = viewModel.CurrentRecordStage;
        RecordAnimeStage.IsVisible = !previewOpen && stage == DownloadRecordStage.AnimeList;
        RecordEpisodeStage.IsVisible = !previewOpen && stage == DownloadRecordStage.EpisodeList;
    }
}
