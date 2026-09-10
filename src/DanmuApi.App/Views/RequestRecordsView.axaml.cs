using Avalonia.Controls;

namespace DanmuApi.App.Views;

public partial class RequestRecordsView : UserControl
{
    public RequestRecordsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode(Bounds.Width);
    }

    internal void UpdateLayoutMode(double width)
    {
        var narrow = width < 860;
        RecordsWorkspace.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,340");
        RecordsWorkspace.RowSpacing = narrow ? 12 : 0;
        Grid.SetRowSpan(RecordTablePanel, narrow ? 1 : 2);
        Grid.SetColumn(RecordDetailsPanel, narrow ? 0 : 1);
        Grid.SetRow(RecordDetailsPanel, narrow ? 1 : 0);
        Grid.SetRowSpan(RecordDetailsPanel, narrow ? 1 : 2);
    }
}
