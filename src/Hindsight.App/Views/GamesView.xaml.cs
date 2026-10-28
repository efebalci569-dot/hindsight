using System.Windows;
using System.Windows.Controls;
using Hindsight.App.ViewModels;

namespace Hindsight.App.Views;

public partial class GamesView : UserControl
{
    private const double CardWidth = 180; // 164 + 16 gap

    public GamesView() => InitializeComponent();

    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is GamesViewModel vm)
        {
            vm.SetColumns((int)((e.NewSize.Width - 24) / CardWidth));
        }
    }
}
