using System.IO;
using System.Text.Json;
using EqLog.Application.Wiki;
using EqLog.Core;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.Mvvm;

namespace EqLog.Overlay.ViewModels;

/// <summary>A tradeskill recipe choice displayed in one of the recipe pickers.</summary>
/// <param name="Recipe">The underlying recipe, or null for a non-selectable progress row.</param>
/// <param name="Label">The display label.</param>
/// <param name="IsEnabled">Whether the choice can be selected.</param>
public sealed record TradeskillRecipePickerItem(TradeskillRecipe? Recipe, string Label, bool IsEnabled = true);

/// <summary>Controls tradeskill search, recipe/combination selection, merchant filtering, and wiki loading.</summary>
public sealed class TradeskillsOverlayViewModel : OverlayViewModel
{
    private readonly ITradeskillsOverlayServices _services;
    private readonly Action<string> _skillChanged;
    private readonly Action<string> _searchChanged;
    private readonly Action<string> _recipeChanged;
    private readonly Action<bool> _sortChanged;
    private readonly Action<bool> _zoneOnlyChanged;
    private readonly Dictionary<string, TradeskillWikiPage> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private TradeskillWikiPage? _catalog;
    private TradeskillRecipe? _recipe;
    private IOverlayWikiLookupLease? _catalogLoad;
    private IOverlayWikiLookupLease? _recipeLoad;
    private bool _attempted;
    private bool _updatingRecipes;
    private bool _updatingCombines;
    private string _loadedSkill;
    private int _persistedSortIndex;
    private TradeskillRecipePickerItem? _selectedRecipeItem;
    private TradeskillRecipePickerItem? _selectedCombineItem;
    private string _skill;
    private string _searchText;
    private string _recipeKey;
    private int _sortIndex;
    private bool _currentZoneOnly;
    private string _status = "";
    private string _catalogStatus = "";
    private long _revision;
    private (long Revision, string? Zone, bool ZoneOnly)? _contentKey;
    private OverlayContent? _content;

    /// <summary>Creates a tradeskills overlay view model.</summary>
    public TradeskillsOverlayViewModel(OverlayDefinition definition, IOverlayActions actions,
        ITradeskillsOverlayServices services, string skill, string searchText, string recipeKey,
        bool sortByTrivial, bool currentZoneOnly, Action<string> skillChanged, Action<string> searchChanged,
        Action<string> recipeChanged, Action<bool> sortChanged, Action<bool> zoneOnlyChanged)
        : base(definition, actions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(skillChanged);
        ArgumentNullException.ThrowIfNull(searchChanged);
        ArgumentNullException.ThrowIfNull(recipeChanged);
        ArgumentNullException.ThrowIfNull(sortChanged);
        ArgumentNullException.ThrowIfNull(zoneOnlyChanged);
        _services = services;
        _skillChanged = skillChanged;
        _searchChanged = searchChanged;
        _recipeChanged = recipeChanged;
        _sortChanged = sortChanged;
        _zoneOnlyChanged = zoneOnlyChanged;
        _skill = NormalizeSkill(skill);
        _loadedSkill = _skill;
        _searchText = searchText ?? "";
        _recipeKey = recipeKey ?? "";
        _sortIndex = sortByTrivial ? 1 : 0;
        _persistedSortIndex = _sortIndex;
        _currentZoneOnly = currentZoneOnly;

        foreach (var page in services.InitialPages)
        {
            _pages[TradeskillQueries.CacheKey(page.Kind, page.Title)] = page;
        }
        if (services.InitialError is { } error)
        {
            _catalogStatus = $"Tradeskill cache could not load: {error.Message}. The original cache is protected until reset or repair.";
        }

        RefreshCommand = new AsyncRelayCommand(() => LoadCatalogAsync(true), () => !_services.WikiLookupPaused);
        OpenWikiCommand = new RelayCommand(OpenWiki);
        SelectSkillCommand = new RelayCommand<object>(SelectSkill);
        SelectSortCommand = new RelayCommand<object>(SelectSort);
        SelectRecipeCommand = new RelayCommand<object>(SelectRecipe, _ => !_updatingRecipes);
        SelectCombineCommand = new RelayCommand<object>(SelectCombine, _ => !_updatingCombines);
        CurrentZoneOnlyCommand = new RelayCommand<object>(SetCurrentZoneOnly);
        RestoreCachedRecipe();
    }

    /// <summary>The available tradeskills.</summary>
    public IReadOnlyList<string> Skills { get; } = TradeskillQueries.Skills;

    /// <summary>The recipe sorting choices.</summary>
    public IReadOnlyList<string> SortOptions { get; } = ["Name only", "Trivial, then name"];

    /// <summary>Filtered catalog recipes and any temporary progress row.</summary>
    public BulkObservableCollection<TradeskillRecipePickerItem> RecipeChoices { get; } = [];

    /// <summary>Alternate combine recipes for the selected outcome.</summary>
    public BulkObservableCollection<TradeskillRecipePickerItem> CombineChoices { get; } = [];

    /// <summary>The selected tradeskill.</summary>
    public string SelectedSkill
    {
        get
        {
            return _skill;
        }
        set
        {
            SetProperty(ref _skill, NormalizeSkill(value));
        }
    }

    /// <summary>The recipe/outcome search text.</summary>
    public string SearchText
    {
        get
        {
            return _searchText;
        }
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                _searchChanged(_searchText);
                RefreshRecipeChoices();
                InvalidateContent();
            }
        }
    }

    /// <summary>The selected recipe picker item.</summary>
    public TradeskillRecipePickerItem? SelectedRecipeItem
    {
        get
        {
            return _selectedRecipeItem;
        }
        set
        {
            SetProperty(ref _selectedRecipeItem, value);
        }
    }

    /// <summary>The selected alternate combine item.</summary>
    public TradeskillRecipePickerItem? SelectedCombineItem
    {
        get
        {
            return _selectedCombineItem;
        }
        set
        {
            SetProperty(ref _selectedCombineItem, value);
        }
    }

    /// <summary>Selected recipe picker index: zero sorts by name and one by trivial then name.</summary>
    public int SortIndex
    {
        get
        {
            return _sortIndex;
        }
        set
        {
            SetProperty(ref _sortIndex, Math.Clamp(value, 0, SortOptions.Count - 1));
        }
    }

    /// <summary>Whether ingredient merchants are limited to the current zone.</summary>
    public bool CurrentZoneOnly
    {
        get
        {
            return _currentZoneOnly;
        }
        set
        {
            if (SetProperty(ref _currentZoneOnly, value))
            {
                _zoneOnlyChanged(value);
                InvalidateContent();
            }
        }
    }

    /// <summary>Whether the catalog request is in progress.</summary>
    public bool IsCatalogLoading
    {
        get
        {
            return _catalogLoad is not null;
        }
    }

    /// <summary>Whether a recipe can currently be selected.</summary>
    public bool IsRecipePickerEnabled
    {
        get
        {
            return !IsCatalogLoading;
        }
    }

    /// <summary>Whether alternate combine choices are available.</summary>
    public bool HasCombineChoices
    {
        get
        {
            return CombineChoices.Count > 1;
        }
    }

    /// <summary>Current catalog or recipe loading status.</summary>
    public string Status
    {
        get
        {
            return _status;
        }
        private set
        {
            if (SetProperty(ref _status, value))
            {
                RefreshRecipeChoices();
                InvalidateContent();
            }
        }
    }

    /// <summary>Catalog cache or loading error status.</summary>
    public string CatalogStatus
    {
        get
        {
            return _catalogStatus;
        }
        private set
        {
            if (SetProperty(ref _catalogStatus, value))
            {
                InvalidateContent();
            }
        }
    }

    /// <summary>Changes the current-zone merchant filter.</summary>
    public RelayCommand<object> CurrentZoneOnlyCommand { get; }

    /// <summary>Loads the current skill and selected recipe with fresh wiki data.</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Opens the selected recipe's wiki page, or the skill page when no recipe is selected.</summary>
    public RelayCommand OpenWikiCommand { get; }

    /// <summary>Loads the chosen tradeskill catalog.</summary>
    public RelayCommand<object> SelectSkillCommand { get; }

    /// <summary>Changes recipe sort order.</summary>
    public RelayCommand<object> SelectSortCommand { get; }

    /// <summary>Loads the selected recipe and its combine information.</summary>
    public RelayCommand<object> SelectRecipeCommand { get; }

    /// <summary>Loads merchant information for the selected alternate combine.</summary>
    public RelayCommand<object> SelectCombineCommand { get; }

    internal void NotifyWikiLookupStateChanged()
    {
        RefreshCommand.RaiseCanExecuteChanged();
    }

    /// <inheritdoc />
    protected override void OnFrame(OverlayFrameMessage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_attempted && !_services.WikiLookupPaused)
        {
            _ = LoadCatalogAsync(false);
        }

        var key = (_revision, frame.Snapshot.Session.Zone, _currentZoneOnly);
        if (_contentKey != key)
        {
            var availability = _recipe is null ? [] : TradeskillQueries.Availability(_recipe, _pages, _errors,
                _currentZoneOnly, frame.Snapshot.Session.Zone);
            string status = string.Join("\n", new[] { _catalogStatus, _status }.Where(text => text.Length > 0));
            _content = BuildTradeskillContent(_skill, _recipe, availability, status, _currentZoneOnly,
                frame.Snapshot.Session.Zone, _catalog is not null);
            _contentKey = key;
        }
        Show(_content!);
    }

    /// <summary>Releases local recipe/cache state after the shell completes a wiki-cache reset.</summary>
    internal void ResetWikiCache()
    {
        _pages.Clear();
        _errors.Clear();
        _catalog = null;
        _recipe = null;
        _recipeKey = "";
        _attempted = false;
        Status = "Wiki cache reset. Reloading when next shown.";
        CatalogStatus = "";
        CombineChoices.Clear();
        OnPropertyChanged(nameof(HasCombineChoices));
        _recipeChanged("");
        InvalidateContent();
    }

    internal static OverlayContent BuildTradeskillContent(string skill, TradeskillRecipe? recipe,
        IReadOnlyList<TradeskillIngredientAvailability> availability, string status, bool zoneOnly, string? zone,
        bool catalogLoaded)
    {
        var rows = new List<OverlayRow>();
        if (status.Length > 0)
        {
            rows.Add(new(status, Wrap: true));
        }
        if (recipe is null)
        {
            rows.Add(new(catalogLoaded ? "Search by recipe or outcome, then choose a recipe above." :
                "Choose a tradeskill to load its recipes.", Wrap: true));
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

    private void SetCurrentZoneOnly(object parameter)
    {
        CurrentZoneOnly = parameter is true;
    }

    private void SelectSkill(object parameter)
    {
        if (parameter is not string skill || skill.Equals(_loadedSkill, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedSkill = NormalizeSkill(skill);
        _loadedSkill = _skill;
        _skillChanged(_skill);
        _recipeKey = "";
        _recipeChanged("");
        _recipe = null;
        SelectedRecipeItem = null;
        SelectedCombineItem = null;
        CombineChoices.Clear();
        OnPropertyChanged(nameof(HasCombineChoices));
        _catalog = null;
        _attempted = false;
        Status = "";
        CatalogStatus = "";
        InvalidateContent();
        _ = LoadCatalogAsync(false);
    }

    private void SelectSort(object parameter)
    {
        if (parameter is not int index)
        {
            return;
        }
        int selected = Math.Clamp(index, 0, SortOptions.Count - 1);
        if (_persistedSortIndex == selected)
        {
            return;
        }

        _persistedSortIndex = selected;
        SortIndex = selected;
        _sortChanged(selected == 1);
        RefreshRecipeChoices();
        InvalidateContent();
    }

    private void SelectRecipe(object parameter)
    {
        if (_updatingRecipes || parameter is not TradeskillRecipePickerItem { Recipe: { } recipe } choice)
        {
            return;
        }
        if (_recipe?.Key == recipe.Key && _recipeKey == recipe.Key)
        {
            return;
        }

        SelectedRecipeItem = choice;
        SelectedCombineItem = null;
        _recipeKey = recipe.Key;
        _recipeChanged(_recipeKey);
        _recipe = recipe;
        _ = LoadTradeskillRecipeAsync(false);
        InvalidateContent();
    }

    private void SelectCombine(object parameter)
    {
        if (_updatingCombines || parameter is not TradeskillRecipePickerItem { Recipe: { } recipe } choice)
        {
            return;
        }
        if (_recipe?.Key == recipe.Key)
        {
            return;
        }

        SelectedCombineItem = choice;
        _recipe = recipe;
        InvalidateContent();
        _ = LoadTradeskillMerchantsAsync(false);
    }

    private void OpenWiki()
    {
        Actions.OpenLink(TradeskillQueries.PageUrl(_recipe?.PageTitle ?? _skill));
    }

    private async Task LoadCatalogAsync(bool refresh)
    {
        if (_services.WikiLookupPaused)
        {
            RestoreCachedRecipe();
            return;
        }
        _catalogLoad?.Cancel();
        _recipeLoad?.Cancel();
        _recipeLoad = null;
        using var lookup = _services.BeginLookup();
        _catalogLoad = lookup;
        OnPropertiesChanged(nameof(IsCatalogLoading), nameof(IsRecipePickerEnabled));
        _attempted = true;
        string skill = _skill;
        Status = $"Loading {skill} recipes...";
        _recipe = null;
        CombineChoices.Clear();
        OnPropertyChanged(nameof(HasCombineChoices));
        RefreshRecipeChoices();
        InvalidateContent();
        try
        {
            var page = await GetTradeskillPageAsync(TradeskillPageKind.Skill, skill, refresh, lookup.Token);
            lookup.Token.ThrowIfCancellationRequested();
            _catalog = page;
            CatalogStatus = TradeskillCacheStatus(TradeskillPageKind.Skill, skill);
            Status = "";
            RefreshRecipeChoices();
        }
        catch (OperationCanceledException) when (lookup.Token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (TradeskillLoadFailure(error))
        {
            _services.TrackWikiError(error);
            CatalogStatus = $"Tradeskill load failed: {error.Message}. Click Refresh to retry.";
            Status = "";
            _catalog ??= _pages.GetValueOrDefault(TradeskillQueries.CacheKey(TradeskillPageKind.Skill, skill));
            RefreshRecipeChoices();
        }
        finally
        {
            if (_catalogLoad == lookup)
            {
                _catalogLoad = null;
                OnPropertiesChanged(nameof(IsCatalogLoading), nameof(IsRecipePickerEnabled));
                RefreshRecipeChoices();
                InvalidateContent();
            }
        }
        if (lookup.Token.IsCancellationRequested)
        {
            return;
        }
        if (SelectedRecipeItem?.Recipe is { } selected)
        {
            _recipeKey = selected.Key;
            _recipe = selected;
            await LoadTradeskillRecipeAsync(refresh);
        }
    }

    private async Task<TradeskillWikiPage> GetTradeskillPageAsync(TradeskillPageKind kind, string title,
        bool refresh, CancellationToken token)
    {
        var page = await _services.GetTradeskillAsync(kind, title, refresh, token);
        token.ThrowIfCancellationRequested();
        _pages[TradeskillQueries.CacheKey(kind, title)] = page;
        InvalidateContent();
        return page;
    }

    private string TradeskillCacheStatus(TradeskillPageKind kind, string title)
    {
        string? error = _services.GetCacheError(TradeskillQueries.CacheKey(kind, title));
        return error is null ? "" : $"Could not save tradeskill cache: {error}. Showing session data.";
    }

    private async Task LoadTradeskillRecipeAsync(bool refresh)
    {
        if (_recipe is not { } recipe)
        {
            return;
        }
        if (_services.WikiLookupPaused)
        {
            if (_pages.TryGetValue(TradeskillQueries.CacheKey(TradeskillPageKind.Recipe, recipe.PageTitle), out var cached))
            {
                ApplyTradeskillCombines(recipe, cached);
            }
            return;
        }
        _recipeLoad?.Cancel();
        using var lookup = _services.BeginLookup();
        _recipeLoad = lookup;
        _updatingCombines = true;
        CombineChoices.Clear();
        SelectedCombineItem = null;
        OnPropertyChanged(nameof(HasCombineChoices));
        _updatingCombines = false;
        Status = $"Loading {recipe.Outcome} combine details...";
        InvalidateContent();
        try
        {
            var page = await GetTradeskillPageAsync(TradeskillPageKind.Recipe, recipe.PageTitle, refresh, lookup.Token);
            lookup.Token.ThrowIfCancellationRequested();
            ApplyTradeskillCombines(recipe, page);
            Status = TradeskillCacheStatus(TradeskillPageKind.Recipe, recipe.PageTitle);
        }
        catch (OperationCanceledException) when (lookup.Token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (TradeskillLoadFailure(error))
        {
            _services.TrackWikiError(error);
            string fallback = "Showing the skill-page recipe";
            if (_pages.TryGetValue(TradeskillQueries.CacheKey(TradeskillPageKind.Recipe, recipe.PageTitle), out var cached))
            {
                ApplyTradeskillCombines(recipe, cached);
                fallback = "Showing previously loaded combine details";
            }
            Status = $"Combine details failed: {error.Message}. {fallback}; click Refresh to retry.";
        }
        finally
        {
            if (_recipeLoad == lookup)
            {
                _recipeLoad = null;
                RefreshRecipeChoices();
                InvalidateContent();
            }
        }
        if (!lookup.Token.IsCancellationRequested)
        {
            await LoadTradeskillMerchantsAsync(refresh);
        }
    }

    private void ApplyTradeskillCombines(TradeskillRecipe recipe, TradeskillWikiPage page)
    {
        var options = TradeskillQueries.CombineOptions(recipe, page);
        _recipe = options[0];
        var choices = options.Select(option => new TradeskillRecipePickerItem(option,
            TradeskillLabel(option, true))).ToArray();
        _updatingCombines = true;
        CombineChoices.ReplaceAll(choices);
        SelectedCombineItem = choices.FirstOrDefault();
        OnPropertyChanged(nameof(HasCombineChoices));
        _updatingCombines = false;
        InvalidateContent();
    }

    private async Task LoadTradeskillMerchantsAsync(bool refresh)
    {
        if (_services.WikiLookupPaused || _recipe is not { } recipe)
        {
            return;
        }
        _recipeLoad?.Cancel();
        using var lookup = _services.BeginLookup();
        _recipeLoad = lookup;
        string previousStatus = _status;
        try
        {
            for (int i = 0; i < recipe.Ingredients.Count; i++)
            {
                var ingredient = recipe.Ingredients[i];
                string key = TradeskillQueries.CacheKey(TradeskillPageKind.Merchants, ingredient.PageTitle);
                Status = $"Loading merchants {i + 1}/{recipe.Ingredients.Count}: {ingredient.Item}";
                try
                {
                    await GetTradeskillPageAsync(TradeskillPageKind.Merchants, ingredient.PageTitle, refresh, lookup.Token);
                    _errors.Remove(key);
                    string cacheError = TradeskillCacheStatus(TradeskillPageKind.Merchants, ingredient.PageTitle);
                    if (cacheError.Length > 0)
                    {
                        _errors[key] = cacheError;
                    }
                }
                catch (Exception error) when (TradeskillLoadFailure(error) && !lookup.Token.IsCancellationRequested)
                {
                    _services.TrackWikiError(error);
                    _errors[key] = error.Message;
                }
                lookup.Token.ThrowIfCancellationRequested();
                InvalidateContent();
            }
        }
        catch (OperationCanceledException) when (lookup.Token.IsCancellationRequested)
        {
        }
        finally
        {
            if (_recipeLoad == lookup)
            {
                _recipeLoad = null;
                Status = previousStatus;
                InvalidateContent();
            }
        }
    }

    private void RefreshRecipeChoices()
    {
        var recipes = TradeskillQueries.Search(_catalog, _searchText, _sortIndex == 1);
        bool catalogLoading = _catalogLoad is not null;
        string loadingStatus = _catalogLoad is not null || _recipeLoad is not null ? _status : "";
        var duplicates = recipes.GroupBy(recipe => recipe.Outcome, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = new List<TradeskillRecipePickerItem>();
        TradeskillRecipePickerItem? selected = null;
        if (catalogLoading)
        {
            choices.Add(new(null, loadingStatus.Length > 0 ? loadingStatus : "Loading recipes...", false));
            selected = choices[0];
        }
        else
        {
            foreach (var recipe in recipes)
            {
                string label = TradeskillLabel(recipe, duplicates.Contains(recipe.Outcome));
                if (recipe.Key.Equals(_recipeKey, StringComparison.Ordinal) && loadingStatus.Length > 0)
                {
                    label = loadingStatus + " - " + label;
                }
                var choice = new TradeskillRecipePickerItem(recipe, label);
                choices.Add(choice);
                if (recipe.Key.Equals(_recipeKey, StringComparison.Ordinal))
                {
                    selected = choice;
                }
            }
            if (selected is null && loadingStatus.Length > 0)
            {
                selected = new TradeskillRecipePickerItem(null, loadingStatus, false);
                choices.Insert(0, selected);
            }
        }

        _updatingRecipes = true;
        RecipeChoices.ReplaceAll(choices);
        SelectedRecipeItem = selected;
        _updatingRecipes = false;
        OnPropertiesChanged(nameof(IsCatalogLoading), nameof(IsRecipePickerEnabled));
    }

    private void RestoreCachedRecipe()
    {
        _catalog = _pages.GetValueOrDefault(TradeskillQueries.CacheKey(TradeskillPageKind.Skill, _skill));
        RefreshRecipeChoices();
        _recipe = SelectedRecipeItem?.Recipe;
        if (_recipe is { } recipe &&
            _pages.TryGetValue(TradeskillQueries.CacheKey(TradeskillPageKind.Recipe, recipe.PageTitle), out var page))
        {
            ApplyTradeskillCombines(recipe, page);
        }
        InvalidateContent();
    }

    private void InvalidateContent()
    {
        _revision++;
        _contentKey = null;
    }

    private static string NormalizeSkill(string? skill)
    {
        return TradeskillQueries.Skills.FirstOrDefault(option =>
            option.Equals(skill, StringComparison.OrdinalIgnoreCase)) ?? TradeskillQueries.Skills[0];
    }

    private static string TradeskillLabel(TradeskillRecipe recipe, bool showIngredients)
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
        return label;
    }

    private static bool TradeskillLoadFailure(Exception error)
    {
        return error is IOException or UnauthorizedAccessException or JsonException or WikiTransportException or
            InvalidOperationException or FormatException or OperationCanceledException or KeyNotFoundException or
            System.Text.RegularExpressions.RegexMatchTimeoutException or OverflowException;
    }
}
