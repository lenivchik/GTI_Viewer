using System.Windows;
using System.Windows.Controls;
using FirebirdViewer.Models;

namespace FirebirdViewer.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionDialog(ConnectionSettings initial)
    {
        InitializeComponent();
        Settings = initial.Clone();
        DataContext = Settings;
        PasswordBox.Password = initial.Password ?? "";
    }

    /// <summary>The settings being edited; updated in-place by the bindings and password handler.</summary>
    public ConnectionSettings Settings { get; }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            Settings.Password = pb.Password;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Settings.Server))
        {
            MessageBox.Show(this, "Укажите адрес сервера.", "Подключение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(Settings.Database))
        {
            MessageBox.Show(this, "Укажите путь к базе данных.", "Подключение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(Settings.UserName))
        {
            MessageBox.Show(this, "Укажите имя пользователя.", "Подключение",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
