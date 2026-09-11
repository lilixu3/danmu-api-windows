using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace DanmuApi.App.Controls;

/// <summary>
/// 让输入框支持「回车提交」：把回车事件转成执行指定命令。
///
/// 表单里回车提交是通用预期（搜索框敲完直接回车、输入令牌后回车登录），
/// 之前每个页面都只能靠鼠标去点旁边那个按钮，键盘流用户很别扭。
/// 做成附加属性而不是逐页写 code-behind：一处实现，各页只在 XAML 上挂一个属性。
///
/// 用法：<c>&lt;TextBox controls:EnterKeyCommand.Command="{Binding SearchCommand}" /&gt;</c>
///
/// 多行输入框（<c>AcceptsReturn=True</c>）不拦截：那里回车本来就该换行。
/// </summary>
public static class EnterKeyCommand
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(EnterKeyCommand));

    static EnterKeyCommand()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, args) => UpdateHandler(control, args));
    }

    public static ICommand? GetCommand(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(CommandProperty);
    }

    public static void SetCommand(Control control, ICommand? value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(CommandProperty, value);
    }

    private static void UpdateHandler(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        // 先把旧的摘掉再按新值决定是否挂：命令换成 null 时必须真的解除订阅。
        control.KeyDown -= OnKeyDown;
        if (args.NewValue is ICommand)
        {
            control.KeyDown += OnKeyDown;
        }
    }

    private static void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || sender is not Control control)
        {
            return;
        }

        // 多行输入框里回车是换行，不抢。
        if (control is TextBox { AcceptsReturn: true })
        {
            return;
        }

        var command = control.GetValue(CommandProperty);
        if (command is null)
        {
            return;
        }

        // 置为已处理，避免同一次回车又冒泡去触发所在对话框的默认按钮，造成一次动作执行两遍。
        args.Handled = true;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
