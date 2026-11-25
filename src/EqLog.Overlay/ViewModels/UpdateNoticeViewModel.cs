using System.Windows.Input;

namespace EqLog.Overlay.ViewModels;

/// <summary>The dock window's "update available" banner: a message and its action button.</summary>
public sealed record UpdateNoticeViewModel(string Text, string ActionText, bool CanAct, ICommand Action)
{
    /// <summary>Builds the banner for an update in the given download state.</summary>
    public static UpdateNoticeViewModel For(string version, bool downloaded, bool busy, ICommand download, ICommand restart)
    {
        string text = downloaded
            ? $"\u2B06 Version {version} is ready"
            : busy
                ? $"\u2B06 Downloading version {version}..."
                : $"\u2B06 Version {version} is available";
        return new UpdateNoticeViewModel(
            text,
            downloaded ? "Restart to update" : "Download update",
            !busy,
            downloaded ? restart : download);
    }
}
