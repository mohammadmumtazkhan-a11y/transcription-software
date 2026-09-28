using System.Windows;

namespace PersonalBATranscriber.App.Views;

public partial class SpellingCorrectionDialog : Window
{
    public string WrongWord { get; }
    public string CorrectedText { get; private set; } = string.Empty;

    public SpellingCorrectionDialog(string wrongWord)
    {
        InitializeComponent();

        WrongWord = wrongWord;
        WrongWordText.Text = $"Misspelled word: \"{wrongWord}\"";
        CorrectionBox.Text = wrongWord;

        Loaded += (s, e) =>
        {
            CorrectionBox.Focus();
            CorrectionBox.SelectAll();
        };
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var text = CorrectionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("Please enter the correct spelling.", "Correction Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CorrectedText = text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
