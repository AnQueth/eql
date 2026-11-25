using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using EqLog.Application.Settings;
using EqLog.Core;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

public sealed class ThemeEditorViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["NightBackground"] = "Window background",
        ["NightSurface"] = "Panel / card background",
        ["NightRaised"] = "Raised controls / headers",
        ["NightBorder"] = "Borders",
        ["NightText"] = "Main text",
        ["NightMuted"] = "Secondary text",
        ["Pumpkin"] = "Accent / headings",
        ["Hover"] = "Hovered controls",
        ["Pressed"] = "Pressed controls",
        ["Selection"] = "Selected rows / text",
        ["Alternate"] = "Alternating table rows",
        ["GridLine"] = "Table grid lines",
        ["ScrollThumb"] = "Scrollbar thumb",
        ["AccentText"] = "Text on accent color",
        ["OverlayHeader"] = "Overlay section headings",
        ["OverlayDim"] = "Overlay secondary rows",
        ["OverlayGood"] = "Overlay good status (spawned, coin)",
        ["OverlayWarn"] = "Overlay warning status (due, expired)",
        ["OverlayBad"] = "Overlay bad status (errors, capped AA)",
    };

    private readonly Dictionary<string, string> _previewColors = new(StringComparer.Ordinal);
    private ThemePalette? _original;
    private string _themeName = "";
    private string _status = "";
    private bool _updatingPalette;

    public ThemeEditorViewModel()
    {
        foreach (string key in ThemePalette.AllColorKeys)
        {
            _previewColors[key] = ThemePalette.OptionalColorDefaults.TryGetValue(key, out string? defaultColor)
                ? defaultColor
                : "#000000";
        }
        Colors = new ObservableCollection<ThemeColorViewModel>(
            ThemePalette.AllColorKeys.Select(key => new ThemeColorViewModel(
                key, Labels[key], UpdateDraft)));
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, onError: SetApplyError);
        ResetCommand = new RelayCommand(ResetDraft);
    }

    public Func<ThemePalette, Task<bool>>? ApplyRequested
    {
        get; set;
    }

    public ObservableCollection<ThemeColorViewModel> Colors
    {
        get;
    }
    public AsyncRelayCommand ApplyCommand
    {
        get;
    }
    public RelayCommand ResetCommand
    {
        get;
    }

    public string ThemeName
    {
        get
        {
            return _themeName;
        }
        set
        {
            if (SetProperty(ref _themeName, value))
            {
                UpdateDraft();
            }
        }
    }

    public string Status
    {
        get
        {
            return _status;
        }
        private set
        {
            SetProperty(ref _status, value);
        }
    }

    public IReadOnlyDictionary<string, Brush> PreviewBrushes
    {
        get
        {
            return _previewColors.ToDictionary(pair => pair.Key,
                pair => (Brush)new SolidColorBrush(ParseColor(pair.Value)));
        }
    }

    public Brush OverlayBackground
    {
        get
        {
            Color color = ParseColor(_previewColors.GetValueOrDefault("NightBackground", "#000000"));
            return new SolidColorBrush(Color.FromArgb(217, color.R, color.G, color.B));
        }
    }

    public void LoadPalette(ThemePalette palette)
    {
        ThemeQueries.Validate(palette);
        _original = ThemeQueries.Copy(palette);
        _updatingPalette = true;
        try
        {
            ThemeName = palette.Name;
            foreach (var color in Colors)
            {
                color.SetValue(ThemeQueries.Color(palette, color.Key));
            }
        }
        finally
        {
            _updatingPalette = false;
        }
        UpdateDraft();
    }

    public ThemePalette ReadPalette()
    {
        var palette = new ThemePalette
        {
            Name = ThemeName.Trim(),
            Colors = Colors.ToDictionary(color => color.Key, color => color.Value.Trim(), StringComparer.Ordinal),
        };
        ThemeQueries.Validate(palette);
        return palette;
    }

    public Color GetPickerInitialColor(ThemeColorViewModel color)
    {
        string value = color.Value.Trim();
        if (IsHexColor(value))
        {
            return ParseColor(value);
        }
        value = _original is null ? "#000000" : ThemeQueries.Color(_original, color.Key);
        Status = "The hex value is invalid. The picker starts from the original color; choosing a color will replace it.";
        return ParseColor(value);
    }

    public void SetPickedColor(ThemeColorViewModel color, Color? selected, string? message)
    {
        if (selected is { } picked)
        {
            color.Value = ColorPickerDialog.ToHex(picked);
        }
        if (message is not null)
        {
            Status = message;
        }
    }

    public async Task ExportAsync(string path)
    {
        try
        {
            var palette = ReadPalette();
            await ThemeComposition.Service.ExportAsync(path, palette);
            Status = $"Exported {palette.Name} to {path}. Active theme unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            Status = $"Could not export theme: {ex.Message}";
        }
    }

    private async Task ApplyAsync()
    {
        try
        {
            var palette = ReadPalette();
            if (ApplyRequested is null)
            {
                Status = "Cannot apply: no theme handler is available.";
            }
            else if (await ApplyRequested(palette))
            {
                LoadPalette(palette);
                Status = $"{palette.Name} applied and saved.";
            }
            else
            {
                Status = "Could not save this theme. See the Theme status above; your draft is retained.";
            }
        }
        catch (FormatException ex)
        {
            Status = $"Cannot apply: {ex.Message}";
        }
    }

    private void ResetDraft()
    {
        if (_original is not null)
        {
            LoadPalette(_original);
        }
    }

    private void UpdateDraft()
    {
        if (_updatingPalette)
        {
            return;
        }
        foreach (var color in Colors)
        {
            color.UpdateSwatch();
        }
        try
        {
            var palette = ReadPalette();
            _previewColors.Clear();
            foreach (string key in ThemePalette.AllColorKeys)
            {
                _previewColors[key] = ThemeQueries.Color(palette, key);
            }
            OnPropertiesChanged(nameof(PreviewBrushes), nameof(OverlayBackground));
            Status = "Valid draft. Apply to save for future starts, or export without changing your active theme.";
        }
        catch (FormatException ex)
        {
            Status = $"Draft needs attention: {ex.Message} Preview shows the last valid palette.";
        }
    }

    private void SetApplyError(Exception error)
    {
        Status = $"Cannot apply: {error.Message}";
    }

    private static bool IsHexColor(string value)
    {
        return value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
    }

    private static Color ParseColor(string value)
    {
        return (Color)ColorConverter.ConvertFromString(value);
    }

    public sealed class ThemeColorViewModel : ObservableObject
    {
        private readonly Action _changed;
        private string _value = "";
        private Brush _swatchBrush = Brushes.Transparent;

        internal ThemeColorViewModel(string key, string label, Action changed)
        {
            Key = key;
            Label = label;
            _changed = changed;
        }

        public string Key
        {
            get;
        }
        public string Label
        {
            get;
        }

        public string Value
        {
            get
            {
                return _value;
            }
            set
            {
                if (!SetProperty(ref _value, value))
                {
                    return;
                }
                UpdateSwatch();
                _changed();
            }
        }

        public Brush SwatchBrush
        {
            get
            {
                return _swatchBrush;
            }
            private set
            {
                SetProperty(ref _swatchBrush, value);
            }
        }

        internal void SetValue(string value)
        {
            _value = value;
            UpdateSwatch();
        }

        internal void UpdateSwatch()
        {
            string value = Value.Trim();
            SwatchBrush = IsHexColor(value) ? new SolidColorBrush(ParseColor(value)) : Brushes.Transparent;
        }
    }
}
