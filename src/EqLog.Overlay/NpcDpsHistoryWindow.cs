using System.Windows;
using EqLog.Application.Sessions;
using EqLog.Overlay.ViewModels;

namespace EqLog.Overlay;

/// <summary>A snapshot comparison of saved loadouts with selectable encounter and effect details.</summary>
public sealed partial class NpcDpsHistoryWindow : Window
{
    public NpcDpsHistoryWindow(NpcReadModel npc)
    {
        InitializeComponent();
        DataContext = new NpcDpsHistoryViewModel(npc);
    }
}
