using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class NpcBrowserOverlayViewModelTests
{
    [Fact]
    public async Task SearchSelectionViewAndHistoryCommand_UpdateObservableState()
    {
        var services = new FakeServices();
        var viewModel = CreateViewModel(services);
        var choice = new NpcChoice("a sphinx", "Skyfire");

        viewModel.SearchText = "sphinx";
        viewModel.SelectedNpc = choice;
        viewModel.ViewIndex = 2;
        viewModel.OpenDpsHistoryCommand.Execute(null);
        await viewModel.OpenDpsHistoryCommand.Completion;

        Assert.Equal("sphinx", services.PersistedSearch);
        Assert.Equal(("a sphinx", "Skyfire"), services.PersistedSelection);
        Assert.Equal(2, services.PersistedView);
        Assert.Equal("No saved encounter history for this NPC and zone yet.", viewModel.DpsStatus);
        Assert.True(viewModel.IsEncounterView);
        Assert.Equal("Page 1 of 1 · 0 encounters", viewModel.PageLabel);
        Assert.Equal(2, services.ReadCount);
    }

    [Fact]
    public void EncounterHistory_LoadingUpdatesPagerCommandsAndPageLabel()
    {
        var services = new FakeServices
        {
            EntryToReturn = CreateNpcWithEncounters(11),
        };
        var viewModel = CreateViewModel(services);
        viewModel.SelectedNpc = new NpcChoice("a sphinx", "Skyfire");

        Assert.True(viewModel.NextPageCommand.CanExecute(null));
        viewModel.NextPageCommand.Execute(null);

        Assert.Equal("Page 2 of 2 · 11 encounters", viewModel.PageLabel);
        Assert.True(viewModel.PreviousPageCommand.CanExecute(null));
        Assert.False(viewModel.NextPageCommand.CanExecute(null));
    }

    private static NpcReadModel CreateNpcWithEncounters(int count)
    {
        var at = new DateTime(2026, 11, 23, 12, 0, 0);
        var encounters = Enumerable.Range(0, count)
            .Select(index => new NpcEncounterDetails(index.ToString(), at.AddMinutes(index), "Tester", null, null,
                NpcEncounterOutcome.Active, 0, 0, 0, 1, 0, []))
            .ToArray();
        return new NpcReadModel("a sphinx", "Skyfire", at, at, 1, null, null, encounters,
            0, 0, 0, 0, 0, 0, false, false);
    }

    [Fact]
    public void ExactNameLookup_UsesMatchingZoneAndStartsWikiLookup()
    {
        var services = new FakeServices();
        var viewModel = CreateViewModel(services);
        viewModel.SearchText = "a sphinx";

        viewModel.LookupNameCommand.Execute(null);

        Assert.Equal("a sphinx", viewModel.SelectedMob);
        Assert.Equal("Skyfire", viewModel.SelectedZone);
        Assert.Equal(1, services.LookupCount);
        Assert.Equal(1, services.LookupTrackingCount);
    }

    private static NpcBrowserOverlayViewModel CreateViewModel(FakeServices services)
    {
        var definition = OverlayPresenters.All.Single(definition => definition.Key == "npcs");
        return new NpcBrowserOverlayViewModel(definition, NullOverlayActions.Instance, services,
            "", null, null, 0, null);
    }

    internal sealed class FakeServices : INpcBrowserOverlayServices
    {
        public long Revision
        {
            get;
            set;
        }

        public bool CanSaveNpcData
        {
            get;
            set;
        } = true;

        public bool CanSaveMobData
        {
            get;
            set;
        } = true;

        public bool WikiLookupPaused
        {
            get;
            set;
        }

        public string? PersistedSearch
        {
            get;
            private set;
        }

        public (string Mob, string Zone)? PersistedSelection
        {
            get;
            private set;
        }

        public int? PersistedView
        {
            get;
            private set;
        }

        public int LookupCount
        {
            get;
            private set;
        }

        public int LookupTrackingCount
        {
            get;
            private set;
        }

        public int ReadCount
        {
            get;
            private set;
        }

        public NpcReadModel? EntryToReturn
        {
            get;
            set;
        }

        public IReadOnlyList<NpcCatalogEntry> SearchNpcs(string filter)
        {
            return [new("a sphinx", "Skyfire", DateTime.UtcNow, DateTime.UtcNow, 1)];
        }

        public Task<NpcReadModel?> GetNpcAsync(string mob, string zone)
        {
            ReadCount++;
            return Task.FromResult(EntryToReturn);
        }

        public MobDetails GetMob(string zone, string mob)
        {
            return new(null, 0, [], []);
        }

        public IReadOnlyList<NpcSpellDetails> NpcSpellDetails(string zone, string mob)
        {
            return [];
        }

        public Task LookupMobAsync(string mob, string zone, bool force, CancellationToken token,
            Action<string> report, WikiPriority priority)
        {
            LookupCount++;
            report("wiki ready");
            return Task.CompletedTask;
        }

        public void OpenMobWiki(string mob, string zone, Action<string> report)
        {
        }

        public void PersistSearch(string search)
        {
            PersistedSearch = search;
        }

        public void PersistSelection(string? mob, string? zone)
        {
            if (mob is not null && zone is not null)
            {
                PersistedSelection = (mob, zone);
            }
        }

        public void PersistView(int view)
        {
            PersistedView = view;
        }

        public void TrackNpcLookup()
        {
            LookupTrackingCount++;
        }

        public void TrackNpcWikiRefresh()
        {
        }

        public void ShowDpsHistory(NpcReadModel npc)
        {
        }
    }
}
