using Avalonia.Controls;
using DanmuApi.App.Services;
using DanmuApi.Core;

namespace DanmuApi.App.Views;

public sealed partial class ConfigurationEditorWindow : Window
{
    public ConfigurationEditorWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ConstrainToWorkingArea();
        CancelButton.Click += (_, _) => Close();
        ClearButton.Click += (_, _) =>
        {
            Result = CoreEnvEditResult.Set(string.Empty);
            Close();
        };
        ResetButton.Click += (_, _) =>
        {
            Result = CoreEnvEditResult.Delete();
            Close();
        };
    }

    public ConfigurationEditorWindow(
        CoreEnvDefinition definition,
        string description,
        Control editor)
        : this($"编辑 {definition.Key}", description, editor)
    {
        ArgumentNullException.ThrowIfNull(definition);
    }

    public ConfigurationEditorWindow(
        string title,
        string description,
        Control editor)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(editor);
        Title = title;
        TitleBlock.Text = title;
        DescriptionBlock.Text = description;
        EditorHost.Content = editor;
    }

    private void ConstrainToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null || screen.Scaling <= 0)
        {
            return;
        }

        var workingWidth = screen.WorkingArea.Width / screen.Scaling;
        var workingHeight = screen.WorkingArea.Height / screen.Scaling;
        MaxWidth = Math.Min(920, Math.Max(MinWidth, workingWidth - 48));
        MaxHeight = Math.Min(720, Math.Max(320, workingHeight - 48));
    }

    public Button SaveActionButton => SaveButton;

    public Button ClearActionButton => ClearButton;

    public Button ResetActionButton => ResetButton;

    public CoreEnvEditResult Result { get; set; } = CoreEnvEditResult.Cancel();

    public bool CanReset
    {
        get => ResetButton.IsVisible;
        set => ResetButton.IsVisible = value;
    }

    public string? ErrorMessage
    {
        get => ErrorBlock.Text;
        set
        {
            ErrorBlock.Text = value ?? string.Empty;
            ErrorBlock.IsVisible = !string.IsNullOrWhiteSpace(value);
        }
    }
}
