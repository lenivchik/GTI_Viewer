using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FirebirdViewer.Metadata;
using FirebirdViewer.Models;
using FirebirdViewer.Services;
using FirebirdViewer.ViewModels;

namespace FirebirdViewer.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(new FirebirdService(), new SettingsService());
        DataContext = _vm;

        _vm.ColumnOrderChanged += (_, _) => SyncColumnOrder();
        _vm.ConnectRequested   += async (_, settings) => await OpenConnectDialogAsync(settings);
        Closed += (_, _) => _vm.SaveSettings();
    }

    // ============================================================
    // File menu — Connect / Exit
    // ============================================================

    private async void ConnectMenu_Click(object sender, RoutedEventArgs e)
    {
        var initial = _vm.Connection.Clone();
        // Pre-fill the demo password if the user hasn't typed one yet.
        if (string.IsNullOrEmpty(initial.Password)) initial.Password = "ehkvfDF5";
        await OpenConnectDialogAsync(initial);
    }

    private async Task OpenConnectDialogAsync(ConnectionSettings initial)
    {
        var dlg = new ConnectionDialog(initial) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        await _vm.ConnectAsync(dlg.Settings).ConfigureAwait(true);
    }

    private void ExitMenu_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        MessageBox.Show(this,
            $"Просмотр данных ГТИ\nВерсия {version}\n\n" +
            "Программа для удобного просмотра данных ГТИ из базы Firebird.\n" +
            "Названия параметров взяты из системы GtiRealtimeCharts.",
            "О программе",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ============================================================
    // Drilling-data DataGrid (Таблица tab)
    // ============================================================

    private void DataGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var col = e.Column;
        var rawName = e.PropertyName;

        // Use the friendly name resolved by the ViewModel (which handles cross-table lookup).
        // SortMemberPath is auto-set to the property name and preserves the raw DB column for
        // sorting and for value lookup; we just override the visual Header text.
        var match = _vm.Columns.FirstOrDefault(c => c.Name == rawName);
        col.Header = match?.DisplayName ?? FriendlyNames.GetColumnDisplay(null, rawName);

        ApplyTwoDecimalFormat(col, e.PropertyType);

        if (match is null) return;

        col.Visibility = match.IsVisible ? Visibility.Visible : Visibility.Collapsed;

        // Live-toggle visibility when the user checks/unchecks the box.
        match.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(ColumnVisibility.IsVisible))
                col.Visibility = match.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    /// <summary>Reorder the visible DataGrid columns to match the VM's Columns collection order.</summary>
    private void SyncColumnOrder()
    {
        if (DataGrid.Columns.Count == 0) return;
        var desired = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _vm.Columns.Count; i++)
            desired[_vm.Columns[i].Name] = i;

        foreach (var col in DataGrid.Columns)
        {
            var raw = col.SortMemberPath;
            if (!string.IsNullOrEmpty(raw) && desired.TryGetValue(raw, out var idx))
                col.DisplayIndex = idx;
        }
    }

    private void DataGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.CurrentCell.Column is null || grid.CurrentCell.Item is null)
        {
            _vm.CursorText = null;
            return;
        }

        var rowIndex = grid.Items.IndexOf(grid.CurrentCell.Item) + 1;
        var headerText = grid.CurrentCell.Column.Header?.ToString() ?? "";
        // SortMemberPath holds the original DB column name on auto-generated columns.
        var rawColumnName = grid.CurrentCell.Column.SortMemberPath;
        if (string.IsNullOrEmpty(rawColumnName)) rawColumnName = headerText;

        var value = "";
        try
        {
            if (grid.CurrentCell.Item is System.Data.DataRowView drv && drv.Row.Table.Columns.Contains(rawColumnName))
                value = drv[rawColumnName]?.ToString() ?? "";
        }
        catch (Exception)
        {
            // Defensive — DataRowView indexers can throw on detached rows; ignore.
        }

        _vm.CursorText = $"Запись {rowIndex} · {headerText}: {value}";
    }

    // ============================================================
    // Operations DataGrid (Операции tab)
    // ============================================================

    private void OperationsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        // The OPERATIONS_VIEW context maps the aliased columns produced by GetOperationsAsync.
        e.Column.Header = FriendlyNames.GetColumnDisplay("OPERATIONS_VIEW", e.PropertyName);
        ApplyTwoDecimalFormat(e.Column, e.PropertyType);
    }

    // ============================================================
    // Chart plot area size → physical zoom limit (1 cm / sec, 1 cm / m)
    // ============================================================

    private void ChartPlot_SizeChanged(object sender, SizeChangedEventArgs e) => ReportPlotArea(sender);
    private void ChartPlot_SizeOrLoad(object sender, RoutedEventArgs e)
    {
        // The plot area is only known after the first render; defer one dispatcher cycle.
        var view = sender as OxyPlot.Wpf.PlotView;
        Dispatcher.BeginInvoke(new System.Action(() => ReportPlotArea(view)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void ReportPlotArea(object? sender)
    {
        if (sender is not OxyPlot.Wpf.PlotView view) return;
        if (view.DataContext is not ChartPanelViewModel vm) return;

        double w = 0, h = 0;
        var area = view.ActualModel?.PlotArea;
        if (area is { Width: > 0, Height: > 0 })
        {
            w = area.Value.Width;
            h = area.Value.Height;
        }
        else
        {
            // Fallback before the first render: control size minus a rough margin.
            w = System.Math.Max(0, view.ActualWidth  - 90);
            h = System.Math.Max(0, view.ActualHeight - 60);
        }

        vm.UpdatePlotAreaSize(w, h);
    }

    /// <summary>For float/double/decimal columns, render values with two decimal places.</summary>
    private static void ApplyTwoDecimalFormat(DataGridColumn column, System.Type propertyType)
    {
        if (column is not DataGridBoundColumn bound || bound.Binding is not Binding binding) return;

        if (IsDecimalType(propertyType))
        {
            binding.StringFormat = "0.00";
        }
        else if (IsDateTimeType(propertyType))
        {
            // 24-hour clock, day-first — matches the Russian convention used by the operator UI.
            binding.StringFormat = "dd.MM.yyyy HH:mm:ss";
        }
    }

    private static bool IsDecimalType(System.Type type)
    {
        var t = System.Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(float) || t == typeof(double) || t == typeof(decimal);
    }

    private static bool IsDateTimeType(System.Type type)
    {
        var t = System.Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(System.DateTime) || t == typeof(System.DateTimeOffset);
    }
}
