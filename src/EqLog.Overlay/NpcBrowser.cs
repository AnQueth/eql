using System.IO;
using EqLog.Application.Sessions;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private bool _npcSaveEnabled = true;
    private string? _npcStartupError;

    private void InitializeNpcCompendium(StartupState state)
    {
        if (_session.NpcsWritable)
        {
            return;
        }
        _npcSaveEnabled = false;
        _npcStartupError = $"NPC compendium could not load: {state.NpcCompendiumError?.Message}. This session is not being saved; the original file is protected.";
    }

    private async Task SaveNpcCompendiumAsync(bool throwOnError)
    {
        if (!_npcSaveEnabled)
        {
            if (throwOnError)
            {
                throw new IOException(_npcStartupError);
            }
            return;
        }
        try
        {
            await _session.SaveNpcsAsync(_tracking);
            SetNpcStorageError(null);
        }
        catch (Exception error) when (!throwOnError && (error is IOException or UnauthorizedAccessException))
        {
            SetNpcStorageError($"NPC compendium save failed: {error.Message}");
        }
    }

    private void SetNpcStorageError(string? error)
    {
        if (_overlayViewModels.TryGetValue("npcs", out var viewModel) &&
            viewModel is ViewModels.NpcBrowserOverlayViewModel npcs)
        {
            npcs.SetStorageError(error);
        }
        if (error is not null)
        {
            SetMobInfoStatus(error);
        }
    }
}
