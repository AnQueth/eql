using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqLog.Core;
using EqLog.Overlay.ViewModels;
using Microsoft.Win32;

namespace EqLog.Overlay;

public partial class ThemeEditor : UserControl
{
    public ThemeEditor()
    {
        InitializeComponent();
    }

    public ThemeEditorViewModel ViewModel
    {
        get
        {
            return (ThemeEditorViewModel)DataContext;
        }
    }

    public void LoadPalette(ThemePalette palette)
    {
        ViewModel.LoadPalette(palette);
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var color = (ThemeEditorViewModel.ThemeColorViewModel)button.DataContext;
        Color selected = ViewModel.GetPickerInitialColor(color);
        ColorPickResult result = ColorPickerDialog.Pick(button, selected);
        ViewModel.SetPickedColor(color, result.Color, result.Message);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export theme JSON",
            Filter = "JSON themes (*.json)|*.json",
            FileName = "CustomTheme.json",
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await ViewModel.ExportAsync(dialog.FileName);
        }
    }
}
