using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FirebirdViewer.Localization;
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

        _vm = new MainViewModel(new FirebirdService(), new SettingsService(), new LiveDataService());
        DataContext = _vm;

        _vm.ColumnOrderChanged += (_, _) => SyncColumnOrder();
        _vm.ConnectRequested   += async (_, settings) => await OpenConnectDialogAsync(settings);
        Loc.LanguageChanged    += OnLanguageChanged;
        Closed += (_, _) =>
        {
            Loc.LanguageChanged -= OnLanguageChanged;
            _vm.SaveSettings();
            _vm.Shutdown();      // stop real-time polling before the app tears down
        };
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
            Loc.F("About_Text", version),
            Loc.T("About_Title"),
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

        _vm.CursorText = Loc.F("Cursor_Record", rowIndex, headerText, value);
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

    private void ToolsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        // The TOOLS_VIEW context maps the aliased columns produced by GetToolsAsync.
        e.Column.Header = FriendlyNames.GetColumnDisplay("TOOLS_VIEW", e.PropertyName);
        ApplyTwoDecimalFormat(e.Column, e.PropertyType);
    }

    // ============================================================
    // Interface language
    // ============================================================

    /// <summary>
    /// Grid headers are assigned once, when a column is generated, so they would keep the old
    /// language. Re-resolve them by the raw column name that SortMemberPath still holds.
    /// Everything else on screen either binds to <see cref="Loc"/> or is rebuilt by the VM.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RelabelColumns(DataGrid,       raw => FriendlyNames.GetColumnDisplay(null, raw));
        RelabelColumns(OperationsGrid, raw => FriendlyNames.GetColumnDisplay("OPERATIONS_VIEW", raw));
        RelabelColumns(ToolsGrid,      raw => FriendlyNames.GetColumnDisplay("TOOLS_VIEW", raw));
    }

    private static void RelabelColumns(DataGrid grid, Func<string, string> label)
    {
        foreach (var col in grid.Columns)
        {
            if (!string.IsNullOrEmpty(col.SortMemberPath))
                col.Header = label(col.SortMemberPath);
        }
    }

    // ============================================================
    // Chart panels — one ChartPlotBinder per WpfPlot instance
    // ============================================================

    private readonly Dictionary<ScottPlot.WPF.WpfPlot, ChartPlotBinder> _chartBinders = new();

    private void ChartPlot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScottPlot.WPF.WpfPlot plot) return;
        if (plot.DataContext is not ChartPanelViewModel vm) return;
        if (_chartBinders.ContainsKey(plot)) return;
        _chartBinders[plot] = new ChartPlotBinder(plot, vm);
    }

    private void ChartPlot_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScottPlot.WPF.WpfPlot plot) return;
        if (_chartBinders.Remove(plot, out var binder)) binder.Dispose();
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
