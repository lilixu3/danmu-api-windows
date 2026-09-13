using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

/// <summary>
/// 「编辑本地弹幕」弹窗的状态。对应核心 1.21.1 的 <c>PATCH /api/v2/local-danmu/{key}</c>：
/// - 单集（<c>scope=resource</c>）：只能改集数与文件名，标题/年份/类型/季属于整组信息；
/// - 整组（<c>scope=group</c>）：改标题/年份/类型/季，组内每个文件保留自己的集数与文件名；
///   改名或改集数后核心会重建 resourceKey，所以保存成功后必须整表刷新。
///
/// 校验文案与核心 <c>validateEditFields</c> / 集数与文件名的检查逐字对齐，
/// 避免界面本地提示和核心 400 的原文出现两套说法。
/// </summary>
public sealed partial class LocalDanmuEditDialogViewModel : ObservableObject
{
    private readonly Func<Task> _submit;

    public LocalDanmuEditDialogViewModel(Func<Task> submit, IReadOnlyList<LocalDanmuTypeOption> typeOptions)
    {
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));
        ArgumentNullException.ThrowIfNull(typeOptions);
        if (typeOptions.Count == 0)
        {
            throw new ArgumentException("类型选项不能为空", nameof(typeOptions));
        }

        TypeOptions = typeOptions;
        _selectedTypeOption = typeOptions[0];
    }

    /// <summary>弹窗请求关闭（保存成功或用户点取消）。</summary>
    public event EventHandler? CloseRequested;

    public IReadOnlyList<LocalDanmuTypeOption> TypeOptions { get; }

    [ObservableProperty]
    private bool _isGroupScope;

    [ObservableProperty]
    private string _heading = string.Empty;

    /// <summary>当前值摘要（改之前长什么样），保存前可对照。</summary>
    [ObservableProperty]
    private string _subjectText = string.Empty;

    [ObservableProperty]
    private string _resourceKey = string.Empty;

    [ObservableProperty]
    private string _groupHintText = string.Empty;

    [ObservableProperty]
    private string _titleInput = string.Empty;

    [ObservableProperty]
    private int? _yearInput;

    [ObservableProperty]
    private LocalDanmuTypeOption _selectedTypeOption;

    [ObservableProperty]
    private int _seasonInput = 1;

    [ObservableProperty]
    private string _episodeText = string.Empty;

    [ObservableProperty]
    private string _fileNameInput = string.Empty;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private bool _canWrite;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsResourceScope => !IsGroupScope;
    public bool HasGroupHint => IsGroupScope && GroupHintText.Length > 0;
    public bool HasValidationMessage => ValidationMessage.Length > 0;
    public string TypeValue => SelectedTypeOption.Value;
    public bool IsMovie => TypeValue == LocalDanmuTypes.Movie;
    public string EpisodeHint => IsMovie ? "电影可留空" : "电视剧必填";

    /// <summary>集数输入 → 数字；留空返回 null（电影表示清空集数）。</summary>
    public int? ParsedEpisode =>
        int.TryParse(EpisodeText?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>本地校验；返回 null 表示可以提交。文案与核心一致。</summary>
    public string? Validate(int currentYear)
    {
        if (!IsGroupScope)
        {
            if (IsMovie)
            {
                return LocalDanmuValidation.ValidateFileName(FileNameInput)
                    ?? (ParsedEpisode is int movieEpisode && movieEpisode <= 0 ? "集数必须是大于 0 的整数" : null);
            }

            if (EpisodeText.Trim().Length == 0)
            {
                return "电视剧集数不能为空";
            }

            return LocalDanmuValidation.ValidateEpisode(ParsedEpisode, isMovie: false)
                ?? LocalDanmuValidation.ValidateFileName(FileNameInput);
        }

        return LocalDanmuValidation.ValidateTitle(TitleInput)
            ?? LocalDanmuValidation.ValidateYear(YearInput, currentYear)
            ?? LocalDanmuValidation.ValidateType(TypeValue)
            ?? LocalDanmuValidation.ValidateSeason(SeasonInput);
    }

    /// <summary>单集编辑：预填当前集数与文件名。</summary>
    public void ShowForResource(CoreLocalDanmuResource resource, bool canWrite)
    {
        ArgumentNullException.ThrowIfNull(resource);
        IsGroupScope = false;
        Heading = $"编辑「{resource.Title}」{LocalDanmuFormatters.EpisodeLabel(resource.Episode, resource.Type)}";
        SubjectText = $"当前：{LocalDanmuFormatters.EpisodeLabel(resource.Episode, resource.Type)} · {resource.Filename}";
        ResourceKey = resource.ResourceKey;
        GroupHintText = string.Empty;
        SelectedTypeOption = TypeOptions.FirstOrDefault(option => option.Value == resource.Type) ?? TypeOptions[0];
        EpisodeText = resource.Episode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        FileNameInput = resource.Filename;
        ValidationMessage = string.Empty;
        CanWrite = canWrite;
        IsBusy = false;
    }

    /// <summary>整组编辑：预填当前标题/年份/类型/季，并说明会影响组内多少文件。</summary>
    public void ShowForGroup(CoreLocalDanmuResource sample, int fileCount, bool canWrite)
    {
        ArgumentNullException.ThrowIfNull(sample);
        IsGroupScope = true;
        Heading = $"编辑「{sample.Title}」的资源信息";
        SubjectText = LocalDanmuFormatters.GroupSubtitle(sample.Year, sample.Type, sample.Season);
        ResourceKey = sample.ResourceKey;
        GroupHintText = fileCount > 0
            ? $"将更新该组的 {fileCount} 个文件；各文件自己的集数与文件名保持不变。"
            : "将更新该组下的全部文件；各文件自己的集数与文件名保持不变。";
        TitleInput = sample.Title;
        YearInput = sample.Year ?? DateTimeOffset.Now.Year;
        SelectedTypeOption = TypeOptions.FirstOrDefault(option => option.Value == sample.Type) ?? TypeOptions[0];
        SeasonInput = sample.Season;
        ValidationMessage = string.Empty;
        CanWrite = canWrite;
        IsBusy = false;
    }

    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>保存成功后由页面 VM 标记，供页面判断是否要顺带关闭来源弹窗。</summary>
    public bool Saved { get; private set; }

    public void MarkSaved() => Saved = true;

    [RelayCommand]
    private Task SubmitAsync() => _submit();

    [RelayCommand]
    private void Close() => RequestClose();

    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasValidationMessage));
    partial void OnGroupHintTextChanged(string value) => OnPropertyChanged(nameof(HasGroupHint));
    partial void OnIsGroupScopeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsResourceScope));
        OnPropertyChanged(nameof(HasGroupHint));
    }

    partial void OnSelectedTypeOptionChanged(LocalDanmuTypeOption value)
    {
        OnPropertyChanged(nameof(TypeValue));
        OnPropertyChanged(nameof(IsMovie));
        OnPropertyChanged(nameof(EpisodeHint));
    }
}
