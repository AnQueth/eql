using System.Windows;
using EqLog.Overlay.ViewModels;

namespace EqLog.Overlay;

public sealed partial class AbilityBreakdownWindow : Window
{
    public AbilityBreakdownWindow(AbilityBreakdown breakdown)
    {
        DataContext = new AbilityBreakdownWindowViewModel(breakdown);
        InitializeComponent();
    }
}
