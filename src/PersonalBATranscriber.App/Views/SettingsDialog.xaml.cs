using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using PersonalBATranscriber.Core.Services;

namespace PersonalBATranscriber.App.Views;

public partial class SettingsDialog : Window
{
    private bool _isKeyVisible = false;

    public string SavedApiKey { get; private set; } = string.Empty;

    public SettingsDialog()
    {
        InitializeComponent();

        var existingKey = CredentialVault.GetApiKey("GROQ_API_KEY");
        if (!string.IsNullOrWhiteSpace(existingKey))
        {
            ApiKeyBox.Password = existingKey;
            ApiKeyTextBox.Text = existingKey;
        }
    }

    private void ToggleVisibilityBtn_Click(object sender, RoutedEventArgs e)
    {
        _isKeyVisible = !_isKeyVisible;

        if (_isKeyVisible)
        {
            ApiKeyTextBox.Text = ApiKeyBox.Password;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ToggleVisibilityBtn.Content = "🙈";
        }
        else
        {
            ApiKeyBox.Password = ApiKeyTextBox.Text;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ApiKeyBox.Visibility = Visibility.Visible;
            ToggleVisibilityBtn.Content = "👁";
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = _isKeyVisible ? ApiKeyTextBox.Text.Trim() : ApiKeyBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("Please enter a valid Groq API key before saving.", "API Key Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CredentialVault.SaveApiKey("GROQ_API_KEY", key);
        SavedApiKey = key;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
