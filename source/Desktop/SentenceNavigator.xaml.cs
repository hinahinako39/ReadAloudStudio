using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using static ReadAloudStudio.UiText;

namespace ReadAloudStudio;

public partial class SentenceNavigator : UserControl
{
    public event Action<int>? Step;
    public SentenceNavigator() => InitializeComponent();

    public void ShowPosition(int index, int count)
    {
        Position.Text = Tr("SentencePosition",index+1,count);
        Previous.IsEnabled = index > 0;
        Next.IsEnabled = index + 1 < count;
    }

    private void PreviousClick(object sender, RoutedEventArgs e) => Step?.Invoke(-1);
    private void NextClick(object sender, RoutedEventArgs e) => Step?.Invoke(1);
    private void NavigateByKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left && Previous.IsEnabled) { Step?.Invoke(-1); e.Handled = true; }
        if (e.Key == Key.Right && Next.IsEnabled) { Step?.Invoke(1); e.Handled = true; }
    }
}
