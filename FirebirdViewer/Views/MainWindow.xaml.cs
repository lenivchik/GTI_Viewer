using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
            $"Firebird — Просмотр данных\nВерсия {version}\n\n" +
            "WPF .NET 8 клиент на основе FirebirdSql.Data.FirebirdClient.",
            "О программе",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ============================================================
    // Column visibility — bind DataGrid columns to the VM model
    // ============================================================

    private void DataGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var match = _vm.Columns.FirstOrDefault(c => c.Name == e.PropertyName);
        if (match is null) return;  // unknown column — leave visible by default

        var col = e.Column;
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
        // Build a map of Name → desired display index
        var desired = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _vm.Columns.Count; i++)
            desired[_vm.Columns[i].Name] = i;

        foreach (var col in DataGrid.Columns)
        {
            var name = col.Header?.ToString();
            if (name is not null && desired.TryGetValue(name, out var idx))
                col.DisplayIndex = idx;
        }
    }

    // ============================================================
    // Cursor — show selected cell info in the status bar
    // ============================================================

    private void DataGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.CurrentCell.Column is null || grid.CurrentCell.Item is null)
        {
            _vm.CursorText = null;
            return;
        }

        var rowIndex = grid.Items.IndexOf(grid.CurrentCell.Item) + 1;
        var colName  = grid.CurrentCell.Column.Header?.ToString() ?? "";
        var value    = "";
        try
        {
            if (grid.CurrentCell.Item is System.Data.DataRowView drv && drv.Row.Table.Columns.Contains(colName))
                value = drv[colName]?.ToString() ?? "";
        }
        catch (Exception)
        {
            // Defensive — DataRowView indexers can throw on detached rows; ignore.
        }

        _vm.CursorText = $"Курсор: строка {rowIndex} · [{colName}] = {value}";
    }
}
