using Avalonia.Controls;

namespace DanmuApi.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutColumns();
    }

    private void UpdateLayoutColumns()
    {
        var narrow = Bounds.Width < 700;
        var serviceWidth = Math.Clamp(Bounds.Width * .28, 280, 340);
        DetailColumns.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : $"{serviceWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)},*");
        Grid.SetColumn(RightPanels, narrow ? 0 : 1);
        Grid.SetRow(RightPanels, narrow ? 1 : 0);
        Grid.SetRow(AddressDetailsPanel, narrow ? 2 : 1);
        Grid.SetColumnSpan(AddressDetailsPanel, narrow ? 1 : 2);
        DetailColumns.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto,Auto" : "Auto,Auto");
        // The trend and core measure at their natural height; the service stretches to the same row bottom.
        ServicePanel.MinHeight = 0;
    }
}
