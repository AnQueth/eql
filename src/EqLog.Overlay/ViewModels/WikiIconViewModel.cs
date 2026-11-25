using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EqLog.Core;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>
/// One item icon or mob portrait, shared by every row showing that name. It starts empty and raises
/// <see cref="Image"/> once the cached wiki image has loaded, so rows bind to it without being rebuilt.
/// </summary>
public sealed class WikiIconViewModel : ObservableObject
{
    private ImageSource? _image;

    /// <summary>Creates an empty icon for <paramref name="key"/>.</summary>
    public WikiIconViewModel(WikiIconKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
    }

    /// <summary>What the icon depicts.</summary>
    public WikiIconKey Key
    {
        get;
    }

    /// <summary>Whether this is a mob portrait, drawn cropped to a square.</summary>
    public bool IsMob
    {
        get
        {
            return Key.Kind == WikiIconKind.Mob;
        }
    }

    /// <summary>The loaded image, or null while loading or when the wiki has none.</summary>
    public ImageSource? Image
    {
        get
        {
            return _image;
        }
        private set
        {
            if (SetProperty(ref _image, value))
            {
                OnPropertyChanged(nameof(HasImage));
            }
        }
    }

    /// <summary>Whether <see cref="Image"/> is available.</summary>
    public bool HasImage
    {
        get
        {
            return _image is not null;
        }
    }

    internal bool IsLoading
    {
        get;
        set;
    }

    internal bool IsResolved
    {
        get;
        set;
    }

    internal DateTime RetryAfterUtc
    {
        get;
        set;
    }

    internal void SetImage(ImageSource? image)
    {
        Image = image;
    }
}

/// <summary>Resolves the shared icon or portrait for a wiki name; set on a view tree through <see cref="Views.WikiLabel"/>.</summary>
public interface IWikiIconSource
{
    /// <summary>Returns the shared icon for <paramref name="key"/>, or null when icons are unavailable.</summary>
    WikiIconViewModel? GetWikiIcon(WikiIconKey key);
}

/// <summary>Hands out shared <see cref="WikiIconViewModel"/>s and fills them from the local icon cache or EQLWiki.</summary>
public sealed class WikiIconProvider : IWikiIconSource
{
    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(10);

    private readonly Func<WikiIconKey, CancellationToken, Task<string?>> _load;
    private readonly Func<bool> _paused;
    private readonly Action<Exception>? _failed;
    private readonly Dictionary<WikiIconKey, WikiIconViewModel> _icons = [];
    private CancellationTokenSource _cancellation = new();

    /// <summary>Creates a provider.</summary>
    /// <param name="load">Returns the local image path for a key, or null when the wiki has no image.</param>
    /// <param name="paused">Whether new lookups must wait, such as during a wiki cache reset.</param>
    /// <param name="failed">Reports unexpected lookup failures.</param>
    public WikiIconProvider(Func<WikiIconKey, CancellationToken, Task<string?>> load, Func<bool> paused,
        Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(paused);
        _load = load;
        _paused = paused;
        _failed = failed;
    }

    /// <summary>Returns the shared icon for <paramref name="key"/>, starting its lookup when needed.</summary>
    public WikiIconViewModel Get(WikiIconKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_icons.TryGetValue(key, out var icon))
        {
            icon = new WikiIconViewModel(key);
            _icons[key] = icon;
        }
        if (!icon.IsLoading && !icon.IsResolved && icon.RetryAfterUtc <= DateTime.UtcNow && !_paused())
        {
            _ = LoadAsync(icon, _cancellation.Token);
        }
        return icon;
    }

    /// <inheritdoc />
    WikiIconViewModel? IWikiIconSource.GetWikiIcon(WikiIconKey key)
    {
        return Get(key);
    }

    /// <summary>Cancels pending lookups after the wiki cache was cleared. Loaded images stay on screen; icons the
    /// wiki had none for are looked up again by <see cref="ResumePending"/>.</summary>
    public void Reset()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _cancellation = new CancellationTokenSource();
        foreach (var icon in _icons.Values)
        {
            if (!icon.HasImage)
            {
                icon.IsResolved = false;
                icon.RetryAfterUtc = DateTime.MinValue;
            }
        }
    }

    /// <summary>Starts lookups for every icon still waiting for an image, such as after a cache reset.</summary>
    public void ResumePending()
    {
        foreach (var icon in _icons.Values.ToArray())
        {
            Get(icon.Key);
        }
    }

    private async Task LoadAsync(WikiIconViewModel icon, CancellationToken token)
    {
        icon.IsLoading = true;
        try
        {
            string? path = await _load(icon.Key, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            ImageSource? image = null;
            if (path is not null)
            {
                image = await Task.Run(() => Decode(path), token);
            }
            icon.SetImage(image);
            icon.IsResolved = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or
            InvalidOperationException or ArgumentException or EqLog.Application.Wiki.WikiTransportException or
            System.Text.Json.JsonException)
        {
            icon.RetryAfterUtc = DateTime.UtcNow + FailureRetry;
            _failed?.Invoke(ex);
        }
        finally
        {
            icon.IsLoading = false;
        }
    }

    /// <summary>Decodes off the UI thread; the frozen image can then be shown on any thread.</summary>
    private static BitmapImage Decode(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
