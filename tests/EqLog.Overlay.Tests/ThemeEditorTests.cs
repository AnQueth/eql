using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EqLog.Application.Settings;
using EqLog.Core;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class ThemeEditorTests
{
    [Fact]
    public void ViewModel_InitializesPreviewBindingsBeforeLoadingPalette()
    {
        var viewModel = new ThemeEditorViewModel();

        Assert.Equal(ThemePalette.AllColorKeys.Length, viewModel.PreviewBrushes.Count);
        foreach (string key in ThemePalette.AllColorKeys)
        {
            Assert.IsType<SolidColorBrush>(viewModel.PreviewBrushes[key]);
        }
        Assert.IsType<SolidColorBrush>(viewModel.OverlayBackground);
    }

    [Fact]
    public async Task ViewModel_LoadsEditsResetsAndAppliesPalette()
    {
        ThemePalette? applied = null;
        var viewModel = new ThemeEditorViewModel
        {
            ApplyRequested = palette =>
            {
                applied = ThemeQueries.Copy(palette);
                return Task.FromResult(true);
            },
        };
        var original = Palette("#112233");
        viewModel.LoadPalette(original);
        Assert.Equal(ThemePalette.AllColorKeys.Length, viewModel.Colors.Count);
        Assert.Equal("#112233", viewModel.ReadPalette().GetColor("NightText"));
        Color preview = Assert.IsType<SolidColorBrush>(viewModel.PreviewBrushes["NightText"]).Color;

        viewModel.Colors.Single(color => color.Key == "NightText").Value = "not-a-color";
        Assert.Contains("Draft needs attention", viewModel.Status);
        Assert.Equal(preview, Assert.IsType<SolidColorBrush>(viewModel.PreviewBrushes["NightText"]).Color);

        viewModel.Colors.Single(color => color.Key == "NightText").Value = "#AABBCC";
        Assert.Equal(Color.FromRgb(0xAA, 0xBB, 0xCC),
            Assert.IsType<SolidColorBrush>(viewModel.PreviewBrushes["NightText"]).Color);
        Assert.Equal(217, Assert.IsType<SolidColorBrush>(viewModel.OverlayBackground).Color.A);

        viewModel.ResetCommand.Execute(null);
        Assert.Equal("#112233", viewModel.ReadPalette().GetColor("NightText"));
        viewModel.ApplyCommand.Execute(null);
        await viewModel.ApplyCommand.Completion;
        Assert.Equal("#112233", applied?.GetColor("NightText"));
        Assert.Contains("applied and saved", viewModel.Status);
    }

    [Fact]
    public void XamlEditor_BindsColorInputsAndInvalidDraftKeepsLastPreview()
    {
        RunSta(() =>
        {
            var editor = new ThemeEditor();
            var window = new Window
            {
                Content = editor,
                Width = 900,
                Height = 800,
            };
            window.Show();
            window.UpdateLayout();
            editor.LoadPalette(Palette("#224466"));
            window.UpdateLayout();

            var input = FindVisualChildren<TextBox>(editor).Single(textBox => Equals(textBox.Tag, "NightText"));
            var previewText = FindVisualChildren<TextBlock>(editor).First(textBlock =>
                textBlock.Text == "Clarity II");
            Assert.Equal(Color.FromRgb(0x22, 0x44, 0x66),
                Assert.IsType<SolidColorBrush>(previewText.Foreground).Color);
            input.Text = "invalid";
            window.UpdateLayout();
            Assert.Contains("Draft needs attention", editor.ViewModel.Status);
            Assert.Equal(Color.FromRgb(0x22, 0x44, 0x66),
                Assert.IsType<SolidColorBrush>(editor.ViewModel.PreviewBrushes["NightText"]).Color);
            Assert.Equal(Color.FromRgb(0x22, 0x44, 0x66),
                Assert.IsType<SolidColorBrush>(previewText.Foreground).Color);

            input.Text = "#ABCDEF";
            window.UpdateLayout();
            Assert.Equal(Color.FromRgb(0xAB, 0xCD, 0xEF),
                Assert.IsType<SolidColorBrush>(editor.ViewModel.PreviewBrushes["NightText"]).Color);
            Assert.Equal(Color.FromRgb(0xAB, 0xCD, 0xEF),
                Assert.IsType<SolidColorBrush>(previewText.Foreground).Color);
            var item = Assert.Single(editor.ViewModel.Colors, color => color.Key == "NightText");
            Assert.Equal(Color.FromRgb(0xAB, 0xCD, 0xEF), Assert.IsType<SolidColorBrush>(item.SwatchBrush).Color);
            window.Close();
        });
    }

    private static ThemePalette Palette(string color)
    {
        return new ThemePalette
        {
            Name = "Test palette",
            Colors = ThemePalette.AllColorKeys.ToDictionary(key => key, _ => color, StringComparer.Ordinal),
        };
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
