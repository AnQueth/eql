namespace EqLog.Overlay;

public partial class MainWindow
{
    private void InitializeExaltations(StartupState state)
    {
        foreach (var result in state.ItemEffects)
        {
            _wikiRepository.SeedItemEffects(result);
        }
    }
}
