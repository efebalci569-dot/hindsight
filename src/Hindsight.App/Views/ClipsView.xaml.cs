using System.Windows;
using System.Windows.Controls;
using Hindsight.App.ViewModels;

namespace Hindsight.App.Views;

public partial class ClipsView : UserControl
{
    private const double CardWidth = 288; // 272 + 16 gap

    public ClipsView() => InitializeComponent();

    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is ClipsViewModel vm)
        {
            vm.SetColumns((int)((e.NewSize.Width - 24) / CardWidth));
        }
    }
}
