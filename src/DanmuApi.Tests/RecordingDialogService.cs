using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

internal sealed class RecordingDialogService : IUiDialogService
{
    public string? RouteSelection { get; set; }
    public bool Confirmation { get; set; }
    public bool CoreSetupAccepted { get; set; }
    public string? PromptText { get; set; }
    public bool CancelProgressOperation { get; set; }
    public CancellationTokenSource? ActiveProgressCancellation { get; private set; }
    public bool CommitDetailsResult { get; set; }
    public bool PullRequestDetailsResult { get; set; }
    public bool UpdateDetailsResult { get; set; }
    public GithubTokenDialogResult GithubTokenResult { get; set; } = GithubTokenDialogResult.Cancel();
    public List<string> RoutePrompts { get; } = [];
    public List<string> SelectedRoutes { get; } = [];
    public List<string> Confirmations { get; } = [];

    /// <summary>确认弹窗的标题与正文（正文里通常带着影响面，例如「将删除 N 个文件」）。</summary>
    public List<(string Title, string Message)> ConfirmationMessages { get; } = [];
    public List<(string Title, string Message, bool IsError)> Messages { get; } = [];
    public List<string> ProgressTitles { get; } = [];
    public List<string> PromptRequests { get; } = [];
    public string? CopiedText { get; private set; }
    public List<string> OpenedUrls { get; } = [];
    public Exception? OpenUrlFailure { get; set; }
    public DanmuFavoriteSchedule? FavoriteScheduleResult { get; set; }
    public bool CancelSaveTextFile { get; set; }
    public Exception? SaveTextFileFailure { get; set; }
    public List<(string FileName, string Content)> SavedFiles { get; } = [];
    public List<(string FileName, byte[] Content, string ContentType)> SavedByteFiles { get; } = [];
    public string? PickedFolder { get; set; }
    public Exception? PickFolderFailure { get; set; }
    public GithubCommitDetails? LastCommitDetails { get; private set; }
    public GithubPullRequest? LastPullRequest { get; private set; }
    public GithubCompareResult? LastComparison { get; private set; }

    public Task EditPortAsync(MainWindowViewModel viewModel) => Task.CompletedTask;
    public Task EditTokenAsync(MainWindowViewModel viewModel) => Task.CompletedTask;
    public Task ShowCacheAsync(MainWindowViewModel viewModel) => Task.CompletedTask;
    public Task<CloseActionDecision?> AskCloseActionAsync() => Task.FromResult<CloseActionDecision?>(null);

    public Task<string?> ChooseGithubRouteAsync(
        string reason,
        string selectedProxyId,
        IGithubProxySpeedTester speedTester,
        CancellationToken cancellationToken = default)
    {
        RoutePrompts.Add(reason);
        if (RouteSelection is not null)
        {
            SelectedRoutes.Add(RouteSelection);
        }

        return Task.FromResult(RouteSelection);
    }

    public Task<string?> ChooseRuntimeRootAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task OpenDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        Confirmations.Add(confirmLabel);
        ConfirmationMessages.Add((title, message));
        return Task.FromResult(Confirmation);
    }

    public Task<bool> ConfirmCoreSetupRequiredAsync(string diagnostic)
    {
        Confirmations.Add("缺核心引导");
        return Task.FromResult(CoreSetupAccepted);
    }

    public Task<string?> SaveTextFileAsync(string suggestedFileName, string content)
    {
        if (SaveTextFileFailure is not null)
        {
            return Task.FromException<string?>(SaveTextFileFailure);
        }

        if (CancelSaveTextFile)
        {
            return Task.FromResult<string?>(null);
        }

        SavedFiles.Add((suggestedFileName, content));
        return Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), suggestedFileName));
    }

    public Task<string?> SaveBytesFileAsync(string suggestedFileName, byte[] content, string contentType)
    {
        if (SaveTextFileFailure is not null)
        {
            return Task.FromException<string?>(SaveTextFileFailure);
        }

        if (CancelSaveTextFile)
        {
            return Task.FromResult<string?>(null);
        }

        SavedByteFiles.Add((suggestedFileName, content.ToArray(), contentType));
        return Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), suggestedFileName));
    }

    public Task<string?> PickFolderAsync(string? initialDirectory)
    {
        if (PickFolderFailure is not null)
        {
            return Task.FromException<string?>(PickFolderFailure);
        }

        return Task.FromResult(PickedFolder);
    }

    /// <summary>按标题选目录（本地弹幕导入用；为空则回落到 <see cref="PickedFolder"/>）。</summary>
    public string? PickedFolderWithTitle { get; set; }

    public List<string> PickedFolderTitles { get; } = [];

    public Task<string?> PickFolderWithTitleAsync(string title, string? initialDirectory)
    {
        PickedFolderTitles.Add(title);
        return Task.FromResult(PickedFolderWithTitle ?? PickedFolder);
    }

    /// <summary>多选文件（本地上传与批量导入用）。</summary>
    public List<string> PickedFiles { get; } = [];

    public List<string> PickedFileTitles { get; } = [];

    public Task<IReadOnlyList<string>> PickFilesAsync(
        string title,
        IReadOnlyList<UiFileFilter> filters,
        bool allowMultiple = false)
    {
        PickedFileTitles.Add(title);
        return Task.FromResult<IReadOnlyList<string>>(PickedFiles.ToArray());
    }

    /// <summary>详情弹窗：记录被打开过几次以及最后一次的模型（并可立即让弹窗自行关闭）。</summary>
    public List<LocalDanmuDetailDialogViewModel> LocalDanmuDetailDialogs { get; } = [];

    public bool CloseLocalDanmuDetailImmediately { get; set; }

    public Task ShowLocalDanmuDetailAsync(LocalDanmuDetailDialogViewModel model)
    {
        LocalDanmuDetailDialogs.Add(model);
        if (CloseLocalDanmuDetailImmediately)
        {
            model.RequestClose();
        }

        return Task.CompletedTask;
    }

    public Task CopyTextAsync(string text)
    {
        CopiedText = text;
        return Task.CompletedTask;
    }

    public Task ShowMessageAsync(string title, string message, bool isError = false)
    {
        Messages.Add((title, message, isError));
        return Task.CompletedTask;
    }

    public Task<string?> PromptTextAsync(string title, string description, string initial, string confirmLabel)
    {
        PromptRequests.Add(title);
        return Task.FromResult(PromptText);
    }

    public Task<DanmuFavoriteSchedule?> PromptFavoriteScheduleAsync(DanmuFavoriteSchedule? current) =>
        Task.FromResult(FavoriteScheduleResult);

    public Task OpenExternalUrlAsync(string url)
    {
        if (OpenUrlFailure is not null)
        {
            return Task.FromException(OpenUrlFailure);
        }

        OpenedUrls.Add(url);
        return Task.CompletedTask;
    }

    public async Task<ProgressOperationResult> RunWithProgressDialogAsync(
        string title,
        Func<IProgress<CoreInstallProgress>, CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ProgressTitles.Add(title);
        if (CancelProgressOperation)
        {
            return new ProgressOperationResult(ProgressOperationOutcome.Canceled, null);
        }

        using var cancellation = new CancellationTokenSource();
        ActiveProgressCancellation = cancellation;
        try
        {
            await operation(new Progress<CoreInstallProgress>(), cancellation.Token);
            return new ProgressOperationResult(ProgressOperationOutcome.Completed, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new ProgressOperationResult(ProgressOperationOutcome.Canceled, null);
        }
        catch (Exception error)
        {
            return new ProgressOperationResult(
                ProgressOperationOutcome.Failed,
                $"{error.GetType().Name}: {error.Message}");
        }
    }

    public Task<bool> ShowCommitDetailsAsync(GithubCommitDetails details)
    {
        LastCommitDetails = details;
        return Task.FromResult(CommitDetailsResult);
    }

    public Task<bool> ShowPullRequestDetailsAsync(GithubPullRequest pullRequest, IReadOnlyList<GithubFileChange> files)
    {
        LastPullRequest = pullRequest;
        return Task.FromResult(PullRequestDetailsResult);
    }

    public Task<bool> ShowUpdateDetailsAsync(GithubCompareResult comparison, string localDisplay, string remoteDisplay)
    {
        LastComparison = comparison;
        return Task.FromResult(UpdateDetailsResult);
    }

    public Task<GithubTokenDialogResult> PromptGithubTokenAsync(bool configured, string hint) =>
        Task.FromResult(GithubTokenResult);

    public Task<bool> ConfirmAdminModeRequiredAsync(string message) =>
        ConfirmAsync("需要管理员模式", message, "前往设置");

    public CoreEnvEditResult CoreEnvEdit { get; set; } = CoreEnvEditResult.Cancel();
    public Exception? CoreEnvEditException { get; set; }
    public List<(string Key, string Initial, bool Configured)> CoreEnvEditRequests { get; } = [];

    public Task<CoreEnvEditResult> PromptCoreEnvEditAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        CoreEnvEditRequests.Add((definition.Key, initial, configured));
        if (CoreEnvEditException is not null)
        {
            return Task.FromException<CoreEnvEditResult>(CoreEnvEditException);
        }

        return Task.FromResult(CoreEnvEdit);
    }
}
