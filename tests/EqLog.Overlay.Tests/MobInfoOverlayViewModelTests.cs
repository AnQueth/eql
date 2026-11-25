using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class MobInfoOverlayViewModelTests
{
    [Fact]
    public void ConsiderAndAssessment_RenderCurrentMobAndAssessment()
    {
        var services = new FakeServices();
        var viewModel = CreateViewModel(services);
        var at = new DateTime(2026, 11, 23, 12, 0, 0);
        var consider = new ConsiderEvent(at, "a sphinx", "Indifferent", "looks like an even match", 58, false);

        viewModel.Receive(new MobConsideredMessage(consider, "Skyfire"));
        viewModel.Receive(new ConsiderAssessmentMessage(
            new ConsiderAssessmentEvent(at.AddSeconds(1), "You could probably win this fight.")));

        Assert.Equal("a sphinx", viewModel.Title);
        Assert.Equal("Skyfire", viewModel.Subtitle);
        Assert.Contains(viewModel.Rows, row => row.Left == "Level 58");
        Assert.Contains(viewModel.Rows, row => row.Left == "You could probably win this fight.");
        Assert.True(viewModel.IsActive);
        Assert.True(viewModel.ConsumeNewConsider());
        Assert.False(viewModel.ConsumeNewConsider());
    }

    [Fact]
    public async Task TimeoutAndResetCommands_UpdateSettingsAndConfirmPersistence()
    {
        var services = new FakeServices();
        var viewModel = CreateViewModel(services);
        var at = new DateTime(2026, 11, 23, 12, 0, 0);
        viewModel.Receive(new MobConsideredMessage(
            new ConsiderEvent(at, "a sphinx", "Indifferent", "even match", 58, false), "Skyfire"));

        viewModel.TimeoutText = "0";
        viewModel.BeginResetCommand.Execute(null);
        Assert.True(viewModel.IsResetConfirmationVisible);
        Assert.Contains("a sphinx in Skyfire", viewModel.ResetConfirmationText);

        viewModel.ConfirmResetCommand.Execute(null);
        await viewModel.ConfirmResetCommand.Completion;

        Assert.Equal(0, services.PersistedTimeout);
        Assert.Equal(("Skyfire", "a sphinx"), services.ResetTarget);
        Assert.False(viewModel.IsResetConfirmationVisible);
        Assert.Contains("history reset", viewModel.StatusText);
    }

    private static MobInfoOverlayViewModel CreateViewModel(FakeServices services)
    {
        var definition = OverlayPresenters.All.Single(definition => definition.Key == "mobinfo");
        return new MobInfoOverlayViewModel(definition, NullOverlayActions.Instance, services, 5,
            "ready");
    }

    internal sealed class FakeServices : IMobInfoOverlayServices
    {
        public bool CanSaveMobData
        {
            get;
            set;
        } = true;

        public bool IsMobInfoEnabled
        {
            get;
            set;
        } = true;

        public double? PersistedTimeout
        {
            get;
            private set;
        }

        public (string Zone, string Mob)? ResetTarget
        {
            get;
            private set;
        }

        public MobDetails GetMob(string zone, string mob)
        {
            return new(null, 0, [], []);
        }

        public Task LookupMobAsync(string mob, string zone, bool force, CancellationToken token,
            Action<string> report, WikiPriority priority)
        {
            report("wiki ready");
            return Task.CompletedTask;
        }

        public void OpenMobWiki(string mob, string zone, Action<string> report)
        {
        }

        public Task ResetResistancesAsync(string zone, string mob)
        {
            ResetTarget = (zone, mob);
            return Task.CompletedTask;
        }

        public void PersistTimeout(double seconds)
        {
            PersistedTimeout = seconds;
        }
    }
}
