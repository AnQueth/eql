using EqLog.Core;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private void InitializeTradeskills(StartupState state)
    {
        foreach (var page in state.Tradeskills)
        {
            _wikiRepository.SeedTradeskill(page);
        }
    }
}
