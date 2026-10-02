using Avalonia.Controls;

namespace DanmuApi.App.Controls;

public sealed class SavedStateCheckBox : CheckBox
{
    protected override Type StyleKeyOverride => typeof(CheckBox);

    protected override void Toggle()
    {
        // The command commits a verified save; input must not replace the bound saved state.
    }
}
