using System.Windows;
using System.Windows.Markup;

namespace ReadAloudStudio;

public partial class LanguageWindow : Window
{
    public string SelectedLanguage => ChineseOption.IsChecked == true ? UiText.Chinese : UiText.English;

    public LanguageWindow()
    {
        InitializeComponent();
        var selected = UiText.LanguageCode == UiText.Chinese ? ChineseOption : EnglishOption;
        selected.IsChecked = true;
        System.Windows.Input.FocusManager.SetFocusedElement(this, selected);
    }

    private void OptionChanged(object sender, RoutedEventArgs e)
    {
        string choice = SelectedLanguage;
        Language = XmlLanguage.GetLanguage(choice);
        Title = UiText.For(choice, "LanguageTitle");
        Prompt.Text = UiText.For(choice, "LanguageHelp");
        ApplyChoice.Content = UiText.For(choice, "LanguageApply");
        CancelChoice.Content = UiText.For(choice, "Cancel");
    }

    private void ConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
