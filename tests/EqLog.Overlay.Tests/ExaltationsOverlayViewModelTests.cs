using EqLog.Application.Wiki;
using EqLog.Core;
using EqLog.Overlay;
using EqLog.Overlay.ViewModels;
using System.Threading.Tasks;
using Xunit;

namespace EqLog.Overlay.Tests;

/// <summary>Tests exaltations filters, refresh commands, and wiki cache service delegation.</summary>
public sealed class ExaltationsOverlayViewModelTests
{
    /// <summary>Refresh retrieves base-item effects interactively and preserves service cache errors.</summary>
    [Fact]
    public async Task RefreshEffectsCommand_LoadsCurrentItemsAndReportsCacheFailure()
    {
        var location = new ExaltationLocation("Drums (Exaltation)", "Drums", 1, true,
            "Drums +6", "Worn, equipment slot 4", "4");
        var services = new FakeExaltationsServices([location]);
        var vm = CreateViewModel(services);
        var changed = new List<string>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? "");
        vm.FilterText = "string resonance";
        vm.SetInstalledOnlyCommand.Execute(true);

        Assert.True(vm.RefreshEffectsCommand.CanExecute(null));
        vm.RefreshEffectsCommand.Execute(null);
        await vm.RefreshEffectsCommand.Completion;

        Assert.Equal("Drums", Assert.Single(services.Lookups).Item);
        Assert.Equal(WikiPriority.Interactive, services.Lookups[0].Priority);
        Assert.True(services.Lookups[0].Force);
        Assert.Equal("Could not save effects: disk full. Results kept for this session.", vm.StorageError);
        Assert.False(vm.IsWorking);
        Assert.Contains(nameof(ExaltationsOverlayViewModel.FilterText), changed);
        Assert.Contains(nameof(ExaltationsOverlayViewModel.InstalledOnly), changed);
        Assert.Contains(nameof(ExaltationsOverlayViewModel.IsWorking), changed);
    }

    private static ExaltationsOverlayViewModel CreateViewModel(FakeExaltationsServices services)
    {
        return new ExaltationsOverlayViewModel(
            OverlayPresenters.All.Single(definition => definition.Key == "exaltations"),
            NullOverlayActions.Instance, services);
    }

    private sealed class FakeExaltationsServices(IReadOnlyList<ExaltationLocation> locations) : IExaltationsOverlayServices
    {
        public IReadOnlyList<ItemEffectInfo> InitialEffects { get; } = [];

        public Exception? InitialError
        {
            get;
        }

        public string? CharacterName
        {
            get
            {
                return "Test Character";
            }
        }

        public IReadOnlyList<ExaltationLocation> ExaltationLocations { get; } = locations;

        public bool HasCurrentInventory
        {
            get
            {
                return true;
            }
        }

        public DateTime? InventoryWritten
        {
            get
            {
                return DateTime.UtcNow;
            }
        }

        public long InventoryRevision
        {
            get
            {
                return 1;
            }
        }

        public bool WikiLookupPaused
        {
            get
            {
                return false;
            }
        }

        public List<(string Item, WikiPriority Priority, bool Force)> Lookups { get; } = [];

        public IOverlayWikiLookupLease BeginLookup()
        {
            return new FakeLookupLease();
        }

        public Task<ItemEffectInfo> GetItemEffectsAsync(string item, WikiPriority priority, bool force,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Lookups.Add((item, priority, force));
            return Task.FromResult(new ItemEffectInfo(item, "https://eqlwiki.com/Drums", DateTime.UtcNow, true,
                [new ItemEffect("Modifier", "String Resonance", null)], "string resonance"));
        }

        public string? GetCacheError(string operation)
        {
            return "disk full";
        }

        public void TrackWikiError(Exception error)
        {
            throw error;
        }
    }

    private sealed class FakeLookupLease : IOverlayWikiLookupLease
    {
        private readonly CancellationTokenSource _cancellation = new();

        public CancellationToken Token
        {
            get
            {
                return _cancellation.Token;
            }
        }

        public Task Completion
        {
            get
            {
                return Task.CompletedTask;
            }
        }

        public void Cancel()
        {
            _cancellation.Cancel();
        }

        public void Dispose()
        {
            _cancellation.Dispose();
        }
    }
}
