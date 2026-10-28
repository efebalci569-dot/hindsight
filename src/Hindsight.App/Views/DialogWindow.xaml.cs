using System.Windows;
using System.Windows.Input;

namespace Hindsight.App.Views;

/// <summary>Themed replacement for MessageBox: confirmations and one-line text prompts.</summary>
public partial class DialogWindow : Window
{
    private DialogWindow(string title, string message, string? input, string okText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        OkButton.Content = okText;
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (Owner is null || !Owner.IsVisible)
        {
            Owner = null;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (input is not null)
        {
            Input.Visibility = Visibility.Visible;
            Input.Text = input;
            Loaded += (_, _) =>
            {
                Input.Focus();
                Input.SelectAll();
            };
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    public static bool Confirm(string title, string message, string okText = "Evet") =>
        new DialogWindow(title, message, null, okText).ShowDialog() == true;

    public static string? Prompt(string title, string message, string initial) =>
        new DialogWindow(title, message, initial, "Kaydet") is var dialog && dialog.ShowDialog() == true ? dialog.Input.Text.Trim() : null;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
