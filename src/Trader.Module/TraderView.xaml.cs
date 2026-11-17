using System.Windows.Controls;
using Trader.Module.ViewModels;

namespace Trader.Module;

public partial class TraderView : UserControl
{
    public TraderView(TraderViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
