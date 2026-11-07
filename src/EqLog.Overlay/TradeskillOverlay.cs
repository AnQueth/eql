using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EqLog.Application.Wiki;
using EqLog.Core;

namespace EqLog.Overlay;

public partial class MainWindow
{
    private readonly Dictionary<string, TradeskillWikiPage> _tradeskillPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tradeskillErrors = new(StringComparer.OrdinalIgnoreCase);
    private TradeskillWikiPage? _tradeskillCatalog;
    private TradeskillRecipe? _tradeskillRecipe;
    private ComboBox? _tradeskillRecipes;
    private ComboBox? _tradeskillCombines;
    private WikiLookupLease? _tradeskillCatalogLoad;
    private WikiLookupLease? _tradeskillRecipeLoad;
    private bool _tradeskillAttempted;
    private bool _tradeskillUpdating;
    private string _tradeskillStatus = "";
    private string _tradeskillCatalogStatus = "";
    private long _tradeskillRevision;
    private (long Revision, string? Zone, bool ZoneOnly)? _tradeskillContentKey;
    private OverlayContent? _tradeskillContent;

    private void InitializeTradeskills(StartupState state)
    {
        foreach (var page in state.Tradeskills)
        {
            _tradeskillPages[TradeskillQueries.CacheKey(page.Kind, page.Title)] = page;
            _wikiRepository.SeedTradeskill(page);
        }
        if (state.TradeskillError is { } error)
        {
            _tradeskillCatalogStatus = $"Tradeskill cache could not load: {error.Message}. The original cache is protected until reset or repair.";
        }
    }

    private void ConfigureTradeskillToolbar(OverlayPanel panel)
    {
        var toolbar = new StackPanel { Margin = new Thickness(0, 5, 0, 4) };
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(6, 0, 0, 0) };
        refresh.ToolTip = "Refresh this tradeskill, the selected recipe, and its ingredient merchants";
        refresh.Click += async (_, _) => await LoadTradeskillCatalogAsync(true);
        var wiki = new Button { Content = "Wiki", Margin = new Thickness(6, 0, 0, 0) };
        wiki.Click += (_, _) => OpenWiki(TradeskillQueries.PageUrl(_tradeskillRecipe?.PageTitle ?? _settings.Tradeskill));
        var skills = new ComboBox
        {
            ItemsSource = TradeskillQueries.Skills,
            ToolTip = "Choose a tradeskill. Unlock floating overlays to interact.",
        };
        skills.SelectedItem = TradeskillQueries.Skills.FirstOrDefault(skill =>
            skill.Equals(_settings.Tradeskill, StringComparison.OrdinalIgnoreCase)) ?? TradeskillQueries.Skills[0];
        _settings.Tradeskill = (string)skills.SelectedItem;
        skills.SelectionChanged += async (_, _) =>
        {
            if (skills.SelectedItem is string skill)
            {
                _settings.Tradeskill = skill;
                _settings.TradeskillRecipe = "";
                _tradeskillCatalog = null;
                _tradeskillRecipe = null;
                await LoadTradeskillCatalogAsync(false);
            }
        };
        var search = new TextBox
        {
            Text = _settings.TradeskillSearch,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Search by recipe name or the item it makes",
        };
        TextFilterBinding.Bind(search, text =>
        {
            _settings.TradeskillSearch = text;
            PopulateTradeskillRecipes();
        });
        var sort = new ComboBox
        {
            ItemsSource = new[] { "Name only", "Trivial, then name" },
            SelectedIndex = _settings.TradeskillSortByTrivial ? 1 : 0,
            ToolTip = "Sort recipes by name or numeric trivial, then name. Unknown trivials sort last.",
        };
        sort.SelectionChanged += (_, _) =>
        {
            if (sort.SelectedIndex >= 0)
            {
                _settings.TradeskillSortByTrivial = sort.SelectedIndex == 1;
                PopulateTradeskillRecipes();
            }
        };
        toolbar.Children.Add(BuildTradeskillFilterRow(skills, search, sort, wiki, refresh));
        _tradeskillRecipes = new ComboBox
        {
            DisplayMemberPath = nameof(TradeskillChoice.Label),
            ToolTip = "Choose a recipe or crafted item; different ingredient combinations remain separate",
            MaxDropDownHeight = 350,
        };
        _tradeskillRecipes.SelectionChanged += async (_, _) =>
        {
            if (!_tradeskillUpdating && _tradeskillRecipes.SelectedItem is TradeskillChoice choice)
            {
                _settings.TradeskillRecipe = choice.Recipe.Key;
                _tradeskillRecipe = choice.Recipe;
                await LoadTradeskillRecipeAsync(false);
            }
        };
        toolbar.Children.Add(_tradeskillRecipes);
        _tradeskillCombines = new ComboBox
        {
            DisplayMemberPath = nameof(TradeskillChoice.Label),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = "Alternate combines listed on the crafted item's wiki page",
        };
        _tradeskillCombines.SelectionChanged += async (_, _) =>
        {
            if (!_tradeskillUpdating && _tradeskillCombines.SelectedItem is TradeskillChoice choice)
            {
                _tradeskillRecipe = choice.Recipe;
                await LoadTradeskillMerchantsAsync(false);
            }
        };
        toolbar.Children.Add(_tradeskillCombines);
        var zone = new CheckBox
        {
            Content = "Current-zone merchants only",
            IsChecked = _settings.TradeskillCurrentZoneOnly,
            Margin = new Thickness(0, 5, 0, 0),
            ToolTip = "Uses the zone from your log. Every ingredient stays visible.",
        };
        zone.Click += (_, _) => _settings.TradeskillCurrentZoneOnly = zone.IsChecked == true;
        toolbar.Children.Add(zone);
        panel.SetToolbar(toolbar);
    }

    internal static Grid BuildTradeskillFilterRow(ComboBox skills, TextBox search, ComboBox sort, Button wiki, Button refresh)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        FrameworkElement[] controls = [skills, search, sort, wiki, refresh];
        for (int i = 0; i < controls.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i < 3 ? new GridLength(i == 2 ? 1.3 : 1, GridUnitType.Star) : GridLength.Auto,
            });
            controls[i].Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0);
            Grid.SetColumn(controls[i], i);
            row.Children.Add(controls[i]);
        }
        return row;
    }

    internal sealed record TradeskillChoice(TradeskillRecipe Recipe, string Label);

    private static TradeskillChoice TradeskillLabel(TradeskillRecipe recipe, bool showIngredients)
    {
        string label = recipe.Name == recipe.Outcome ? recipe.Outcome : $"{recipe.Name} -> {recipe.Outcome}";
        if (recipe.Trivial.Length > 0)
        {
            label += $" (trivial {recipe.Trivial})";
        }
        if (showIngredients && recipe.Ingredients.Count > 0)
        {
            label += " - " + string.Join(", ", recipe.Ingredients.Select(item => $"{item.Quantity}x {item.Item}"));
        }
        return new(recipe, label);
    }

    private void PopulateTradeskillRecipes()
    {
        if (_tradeskillRecipes is null)
        {
            return;
        }
        var recipes = TradeskillQueries.Search(_tradeskillCatalog, _settings.TradeskillSearch,
            _settings.TradeskillSortByTrivial);
        string loadingStatus = _tradeskillCatalogLoad is not null || _tradeskillRecipeLoad is not null
            ? _tradeskillStatus : "";
        _tradeskillUpdating = true;
        UpdateTradeskillRecipePicker(_tradeskillRecipes, recipes, _settings.TradeskillRecipe,
            loadingStatus, _tradeskillCatalogLoad is not null);
        _tradeskillUpdating = false;
        _tradeskillRevision++;
    }

    internal static void UpdateTradeskillRecipePicker(ComboBox picker, IReadOnlyList<TradeskillRecipe> recipes,
        string selectedKey, string loadingStatus, bool catalogLoading)
    {
        picker.IsEnabled = !catalogLoading;
        if (catalogLoading)
        {
            picker.ItemsSource = new[] { new ComboBoxItem
            {
                Content = loadingStatus.Length > 0 ? loadingStatus : "Loading recipes...",
                IsEnabled = false,
            } };
            picker.SelectedIndex = 0;
            return;
        }
        var duplicates = recipes.GroupBy(recipe => recipe.Outcome, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = recipes.Select(recipe =>
        {
            var choice = TradeskillLabel(recipe, duplicates.Contains(recipe.Outcome));
            return recipe.Key == selectedKey && loadingStatus.Length > 0
                ? choice with { Label = loadingStatus + " - " + choice.Label } : choice;
        }).ToArray();
        var selected = choices.FirstOrDefault(choice => choice.Recipe.Key == selectedKey);
        if (selected is null && loadingStatus.Length > 0)
        {
            var progress = new ComboBoxItem { Content = loadingStatus, IsEnabled = false };
            picker.ItemsSource = new object[] { progress }.Concat(choices).ToArray();
            picker.SelectedItem = progress;
        }
        else
        {
            picker.ItemsSource = choices;
            picker.SelectedItem = selected;
        }
    }

    private async Task LoadTradeskillCatalogAsync(bool refresh)
    {
        if (WikiLookupPaused)
        {
            return;
        }
        _tradeskillCatalogLoad?.Cancel();
        _tradeskillRecipeLoad?.Cancel();
        _tradeskillRecipeLoad = null;
        using var lookup = BeginWikiLookup();
        _tradeskillCatalogLoad = lookup;
        _tradeskillAttempted = true;
        string skill = _settings.Tradeskill;
        _tradeskillStatus = $"Loading {skill} recipes...";
        _tradeskillRecipe = null;
        if (_tradeskillCombines is not null)
        {
            _tradeskillCombines.Visibility = Visibility.Collapsed;
        }
        PopulateTradeskillRecipes();
        _tradeskillRevision++;
        try
        {
            var page = await GetTradeskillPageAsync(TradeskillPageKind.Skill, skill, refresh, lookup.Token);
            lookup.Token.ThrowIfCancellationRequested();
            _tradeskillCatalog = page;
            _tradeskillCatalogStatus = TradeskillCacheStatus(TradeskillPageKind.Skill, skill);
            _tradeskillStatus = "";
            PopulateTradeskillRecipes();
        }
        catch (OperationCanceledException) when (lookup.Token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (TradeskillLoadFailure(error))
        {
            UsageTelemetry.TrackError(error, UsageErrorSource.Wiki);
            _tradeskillCatalogStatus = $"Tradeskill load failed: {error.Message}. Click Refresh to retry.";
            _tradeskillStatus = "";
            _tradeskillCatalog ??= _tradeskillPages.GetValueOrDefault(TradeskillQueries.CacheKey(TradeskillPageKind.Skill, skill));
            PopulateTradeskillRecipes();
        }
        finally
        {
            if (_tradeskillCatalogLoad == lookup)
            {
                _tradeskillCatalogLoad = null;
                PopulateTradeskillRecipes();
                _tradeskillRevision++;
            }
        }
        if (_tradeskillRecipes?.SelectedItem is TradeskillChoice choice)
        {
            _tradeskillRecipe = choice.Recipe;
            await LoadTradeskillRecipeAsync(refresh);
        }
    }

    private async Task<TradeskillWikiPage> GetTradeskillPageAsync(TradeskillPageKind kind, string title,
        bool refresh, CancellationToken token)
    {
        var page = await _wikiRepository.GetTradeskillAsync(kind, title, WikiPriority.Interactive, refresh, token);
        token.ThrowIfCancellationRequested();
        _tradeskillPages[TradeskillQueries.CacheKey(kind, title)] = page;
        _tradeskillRevision++;
        return page;
    }

    private string TradeskillCacheStatus(TradeskillPageKind kind, string title)
    {
        string? error = _wikiRepository.GetCacheError(TradeskillQueries.CacheKey(kind, title));
        return error is null ? "" : $"Could not save tradeskill cache: {error}. Showing session data.";
    }

    private async Task LoadTradeskillRecipeAsync(bool refresh)
    {
        if (WikiLookupPaused || _tradeskillRecipe is not { } recipe)
        {
            return;
        }
        _tradeskillRecipeLoad?.Cancel();
        using var lookup = BeginWikiLookup();
        _tradeskillRecipeLoad = lookup;
        if (_tradeskillCombines is not null)
        {
            _tradeskillCombines.Visibility = Visibility.Collapsed;
        }
        _tradeskillStatus = $"Loading {recipe.Outcome} combine details...";
        PopulateTradeskillRecipes();
        _tradeskillRevision++;
        try
        {
            var page = await GetTradeskillPageAsync(TradeskillPageKind.Recipe, recipe.PageTitle, refresh, lookup.Token);
            lookup.Token.ThrowIfCancellationRequested();
            ApplyTradeskillCombines(recipe, page);
            _tradeskillStatus = TradeskillCacheStatus(TradeskillPageKind.Recipe, recipe.PageTitle);
        }
        catch (OperationCanceledException) when (lookup.Token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (TradeskillLoadFailure(error))
        {
            UsageTelemetry.TrackError(error, UsageErrorSource.Wiki);
            string fallback = "Showing the skill-page recipe";
            if (_tradeskillPages.TryGetValue(TradeskillQueries.CacheKey(TradeskillPageKind.Recipe, recipe.PageTitle), out var cached))
            {
                ApplyTradeskillCombines(recipe, cached);
                fallback = "Showing previously loaded combine details";
            }
            _tradeskillStatus = $"Combine details failed: {error.Message}. {fallback}; click Refresh to retry.";
        }
        finally
        {
            if (_tradeskillRecipeLoad == lookup)
            {
                _tradeskillRecipeLoad = null;
                PopulateTradeskillRecipes();
                _tradeskillRevision++;
            }
        }
        await LoadTradeskillMerchantsAsync(refresh);
    }

    private void ApplyTradeskillCombines(TradeskillRecipe recipe, TradeskillWikiPage page)
    {
        var options = TradeskillQueries.CombineOptions(recipe, page);
        _tradeskillRecipe = options[0];
        if (_tradeskillCombines is not null)
        {
            _tradeskillUpdating = true;
            _tradeskillCombines.ItemsSource = options.Select(option => TradeskillLabel(option, true)).ToArray();
            _tradeskillCombines.SelectedIndex = 0;
            _tradeskillCombines.Visibility = options.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _tradeskillUpdating = false;
        }
    }

    private async Task LoadTradeskillMerchantsAsync(bool refresh)
    {
        if (WikiLookupPaused || _tradeskillRecipe is not { } recipe)
        {
            return;
        }
        _tradeskillRecipeLoad?.Cancel();
        using var lookup = BeginWikiLookup();
        _tradeskillRecipeLoad = lookup;
        string previousStatus = _tradeskillStatus;
        try
        {
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                var ingredient = recipe.Ingredients[i];
                string key = TradeskillQueries.CacheKey(TradeskillPageKind.Merchants, ingredient.PageTitle);
                _tradeskillStatus = $"Loading merchants {i + 1}/{recipe.Ingredients.Count}: {ingredient.Item}";
                PopulateTradeskillRecipes();
                _tradeskillRevision++;
                try
                {
                    await GetTradeskillPageAsync(TradeskillPageKind.Merchants, ingredient.PageTitle, refresh, lookup.Token);
                    _tradeskillErrors.Remove(key);
                    string cacheError = TradeskillCacheStatus(TradeskillPageKind.Merchants, ingredient.PageTitle);
                    if (cacheError.Length > 0)
                    {
                        _tradeskillErrors[key] = cacheError;
                    }
                }
                catch (Exception error) when (TradeskillLoadFailure(error) && !lookup.Token.IsCancellationRequested)
                {
                    UsageTelemetry.TrackError(error, UsageErrorSource.Wiki);
                    _tradeskillErrors[key] = error.Message;
                }
                lookup.Token.ThrowIfCancellationRequested();
                _tradeskillRevision++;
            }
        }
        catch (OperationCanceledException) when (lookup.Token.IsCancellationRequested)
        {
        }
        finally
        {
            if (_tradeskillRecipeLoad == lookup)
            {
                _tradeskillRecipeLoad = null;
                _tradeskillStatus = previousStatus;
                PopulateTradeskillRecipes();
                _tradeskillRevision++;
            }
        }
    }

    private static bool TradeskillLoadFailure(Exception error)
    {
        return error is IOException or UnauthorizedAccessException or JsonException or WikiTransportException or
            InvalidOperationException or FormatException or OperationCanceledException or KeyNotFoundException or
            System.Text.RegularExpressions.RegexMatchTimeoutException or OverflowException;
    }

    private OverlayContent TradeskillContent(string? zone)
    {
        if (!_tradeskillAttempted && !WikiLookupPaused)
        {
            _ = LoadTradeskillCatalogAsync(false);
        }
        var key = (_tradeskillRevision, zone, _settings.TradeskillCurrentZoneOnly);
        if (_tradeskillContentKey != key)
        {
            var availability = _tradeskillRecipe is null ? [] : TradeskillQueries.Availability(_tradeskillRecipe,
                _tradeskillPages, _tradeskillErrors, _settings.TradeskillCurrentZoneOnly, zone);
            _tradeskillContent = BuildTradeskillContent(_settings.Tradeskill, _tradeskillRecipe, availability,
                string.Join("\n", new[] { _tradeskillCatalogStatus, _tradeskillStatus }.Where(text => text.Length > 0)),
                _settings.TradeskillCurrentZoneOnly, zone, _tradeskillCatalog is not null);
            _tradeskillContentKey = key;
        }
        return _tradeskillContent!;
    }

    internal static OverlayContent BuildTradeskillContent(string skill, TradeskillRecipe? recipe,
        IReadOnlyList<TradeskillIngredientAvailability> availability, string status, bool zoneOnly, string? zone, bool catalogLoaded)
    {
        var rows = new List<OverlayRow>();
        if (status.Length > 0)
        {
            rows.Add(new(status, Wrap: true));
        }
        if (recipe is null)
        {
            rows.Add(new(catalogLoaded ? "Search by recipe or outcome, then choose a recipe above." : "Choose a tradeskill to load its recipes.", Wrap: true));
        }
        else
        {
            rows.Add(new(recipe.Outcome, Bold: true, Wrap: true, Link: TradeskillQueries.PageUrl(recipe.PageTitle)));
            rows.Add(new($"Recipe: {recipe.Name} | Trivial: {(recipe.Trivial.Length > 0 ? recipe.Trivial : "Unknown")}", Wrap: true));
            rows.Add(new($"Combine in: {(recipe.Container.Length > 0 ? recipe.Container : "Not listed on the item page")}" +
                (recipe.Yield is { } yield ? $" | Yield: {yield}" : ""), Wrap: true));
            if (recipe.Ingredients.Count == 0)
            {
                rows.Add(new("The item template does not list ingredients. Open Wiki for any additional recipe information.", Wrap: true));
            }
            foreach (var item in availability)
            {
                rows.Add(new($"{item.Ingredient.Quantity}x {item.Ingredient.Item}", Bold: true, Wrap: true,
                    Link: TradeskillQueries.PageUrl(item.Ingredient.PageTitle)));
                if (item.Ingredient.Source.Length > 0)
                {
                    rows.Add(new($"Source: {item.Ingredient.Source}", Wrap: true));
                }
                if (item.Error is { } error)
                {
                    rows.Add(new($"Merchant lookup/cache error: {error}. Click Refresh to retry.", Wrap: true));
                }
                string? note = item.Status switch
                {
                    TradeskillMerchantStatus.NotLoaded => "Merchant lookup pending...",
                    TradeskillMerchantStatus.Failed => "Merchant information unavailable.",
                    TradeskillMerchantStatus.NoListedMerchants => "No merchants listed on the wiki. May require crafting, looting, foraging, or another source.",
                    TradeskillMerchantStatus.ZoneUnknown => "Current zone unknown. Open a character log or turn off the zone filter.",
                    TradeskillMerchantStatus.NoneInZone => "No merchants listed in your current zone.",
                    _ => null,
                };
                if (note is not null)
                {
                    rows.Add(new(note, Wrap: true));
                }
                foreach (var merchant in item.Merchants)
                {
                    rows.Add(new($"{merchant.Name} - {merchant.Zone}", Wrap: true,
                        Link: TradeskillQueries.PageUrl(merchant.PageTitle),
                        Details: string.Join(" | ", new[] { merchant.Area, merchant.Location }.Where(text => text.Length > 0)),
                        ToolTip: "Open this merchant's EQLWiki page"));
                }
            }
        }
        string subtitle = zoneOnly ? $"{skill} | Current zone: {zone ?? "unknown"}" : $"{skill} | All zones";
        return new("Tradeskills", subtitle, rows, TradeskillQueries.PageUrl(skill));
    }

    private void ResetTradeskillView()
    {
        _tradeskillPages.Clear();
        _tradeskillErrors.Clear();
        _tradeskillCatalog = null;
        _tradeskillRecipe = null;
        _tradeskillAttempted = false;
        _tradeskillStatus = "Wiki cache reset. Reloading when next shown.";
        _tradeskillCatalogStatus = "";
        if (_tradeskillCombines is not null)
        {
            _tradeskillCombines.Visibility = Visibility.Collapsed;
        }
        PopulateTradeskillRecipes();
        _tradeskillRevision++;
    }
}
