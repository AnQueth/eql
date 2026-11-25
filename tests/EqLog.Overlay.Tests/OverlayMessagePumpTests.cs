using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Triggers;
using EqLog.Overlay.Messaging;
using EqLog.Overlay.ViewModels;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class OverlayMessagePumpTests
{
    [Fact]
    public void PumpOnce_DispatchesQueuedTickFramesAndCompletionInOrder()
    {
        RunOnDispatcher(async () =>
        {
            var feed = new FakeLogFeed();
            using var pump = new OverlayMessagePump(feed);
            var log = new List<string>();
            var spawns = new FakeFrameReceiver("spawns", log, new FrameRequirements(Spawns: true));
            var hidden = new FakeFrameReceiver("hidden", log, new FrameRequirements(SpellDurations: true)) { WantsFrames = false };
            var chat = new FakeFrameReceiver("chat", log, new FrameRequirements(new HashSet<ChatChannel> { ChatChannel.Guild }));
            pump.Subscribe<OverlayFrameMessage>(spawns);
            pump.Subscribe<OverlayFrameMessage>(hidden);
            pump.Subscribe<OverlayFrameMessage>(chat);
            pump.Subscribe<OverlayTickMessage>(_ => log.Add("tick"));
            pump.Subscribe<MobConsideredMessage>(message => log.Add("consider " + message.Zone));
            pump.Subscribe<OverlayFrameCompletedMessage>(_ => log.Add("completed"));

            feed.RaiseConsider("Nektulos");
            Assert.Empty(log);

            Assert.True(await pump.PumpOnceAsync());

            Assert.Equal(new[] { "consider Nektulos", "tick", "spawns", "chat", "completed" }, log);
            var requirements = Assert.Single(feed.Captures);
            Assert.True(requirements.Spawns);
            Assert.False(requirements.SpellDurations);
            Assert.Equal(new[] { ChatChannel.Guild }, requirements.ChatChannels!);
            Assert.Same(spawns.Frames[0], chat.Frames[0]);
        });
    }

    [Fact]
    public void PumpOnce_DiscardsFrameWhenLogChangesDuringCapture()
    {
        RunOnDispatcher(async () =>
        {
            var feed = new FakeLogFeed();
            using var pump = new OverlayMessagePump(feed);
            var log = new List<string>();
            var receiver = new FakeFrameReceiver("overlay", log, FrameRequirements.None);
            pump.Subscribe<OverlayFrameMessage>(receiver);
            pump.Subscribe<OverlayFrameCompletedMessage>(_ => log.Add("completed"));
            feed.OnCapture = () => feed.Revision++;

            Assert.False(await pump.PumpOnceAsync());
            Assert.Empty(log);

            feed.OnCapture = null;
            Assert.True(await pump.PumpOnceAsync());
            Assert.Equal(new[] { "overlay", "completed" }, log);
        });
    }

    [Fact]
    public void ReaderEvents_DeliverBackgroundMessagesNowAndQueueLiveAlerts()
    {
        RunOnDispatcher(async () =>
        {
            var feed = new FakeLogFeed();
            var pump = new OverlayMessagePump(feed);
            var batches = new List<LogBatchMessage>();
            var spells = new List<string>();
            var alerts = new List<TriggerAlertsMessage>();
            pump.SubscribeBackground<LogBatchMessage>(batches.Add);
            var spellSubscription = pump.SubscribeBackground<SpellObservedMessage>(message => spells.Add(message.Spell));
            pump.Subscribe<TriggerAlertsMessage>(alerts.Add);
            feed.Alerts = [Alert("Hello", sound: false)];

            feed.RaiseBatch(live: false);
            Assert.Single(batches);
            Assert.Equal(0, feed.TriggerMatches);

            feed.RaiseBatch(live: true);
            Assert.Equal(2, batches.Count);
            Assert.Equal(1, feed.TriggerMatches);
            Assert.Empty(alerts);
            await pump.PumpOnceAsync();
            Assert.Same(feed.Alerts[0], Assert.Single(Assert.Single(alerts).Alerts));

            feed.RaiseSpell("Spirit of Wolf");
            spellSubscription.Dispose();
            feed.RaiseSpell("Levitate");
            Assert.Equal(new[] { "Spirit of Wolf" }, spells);

            pump.Dispose();
            feed.RaiseBatch(live: true);
            Assert.Equal(2, batches.Count);
            Assert.False(await pump.PumpOnceAsync());
        });
    }

    [Fact]
    public void AlertsOverlay_KeepsNewestAlertsUntilTheyExpire()
    {
        RunOnDispatcher(() =>
        {
            var definition = Assert.Single(OverlayPresenters.All, item => item.Key == "alerts");
            var now = new DateTime(2026, 11, 1, 20, 0, 0);
            int sounds = 0;
            var vm = new AlertsOverlayViewModel(definition, NullOverlayActions.Instance, EmptyOverlayViewSource.Instance,
                () => sounds++, _ =>
                {
                }, () => now);

            vm.Receive(new TriggerAlertsMessage([Alert("Quiet", sound: false, seconds: 2)]));
            Assert.Equal(0, sounds);
            vm.Receive(new TriggerAlertsMessage(Enumerable.Range(1, AlertsOverlayViewModel.MaxAlerts)
                .Select(i => Alert("Loud " + i, sound: i == 1, seconds: 10)).ToArray()));
            Assert.Equal(1, sounds);
            Assert.Equal(AlertsOverlayViewModel.MaxAlerts, vm.ActiveAlerts.Count);
            Assert.Equal("Loud 1", vm.ActiveAlerts[0].Text);

            now = now.AddSeconds(11);
            vm.Receive(new TriggerAlertsMessage([Alert("Later", sound: false)]));
            Assert.Equal("Later", Assert.Single(vm.ActiveAlerts).Text);
            return Task.CompletedTask;
        });
    }

    private static TriggerAlert Alert(string text, bool sound, double seconds = 8)
    {
        var trigger = new AlertTrigger { Name = text, Simple = text, Text = text, Sound = sound, Seconds = seconds };
        return new TriggerAlert(trigger, Regex.Match(text, ".+"), "Que", new DateTime(2026, 11, 1), text);
    }

    /// <summary>Runs <paramref name="test"/> on a fresh STA dispatcher thread so pump awaits resume on it.</summary>
    private static void RunOnDispatcher(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await test();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The dispatcher test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class FakeFrameReceiver(string name, List<string> log, FrameRequirements requirements) : IOverlayFrameReceiver
    {
        public bool WantsFrames { get; set; } = true;

        public FrameRequirements Requirements
        {
            get
            {
                return requirements;
            }
        }

        public List<OverlayFrameMessage> Frames { get; } = [];

        public void Receive(OverlayFrameMessage message)
        {
            Frames.Add(message);
            log.Add(name);
        }
    }

    private sealed class FakeLogFeed : IOverlayLogFeed
    {
        private readonly LogEngine _engine = new();

        public event Action<LogSessionBatch>? BatchProcessed;

        public event Action<ConsiderEvent, string>? LiveMobConsidered;

        public event Action<ConsiderAssessmentEvent>? LiveConsiderAssessment
        {
            add
            {
            }
            remove
            {
            }
        }

        public event Action<string>? SpellObserved;

        public TailProgress Progress { get; set; } = new(10, 10, 1, true);

        public Task? Completion
        {
            get
            {
                return null;
            }
        }

        public long Revision
        {
            get; set;
        }

        public List<FrameRequirements> Captures { get; } = [];

        public Action? OnCapture
        {
            get; set;
        }

        public IReadOnlyList<TriggerAlert> Alerts { get; set; } = [];

        public int TriggerMatches
        {
            get; private set;
        }

        public async Task<LogSessionFrame> CaptureFrameAsync(FrameRequirements requirements)
        {
            Captures.Add(requirements);
            await Task.Yield();
            OnCapture?.Invoke();
            return new LogSessionFrame(_engine.Snapshot(DateTime.Now), [], false, EngineQuerySnapshot.Capture(_engine));
        }

        public IReadOnlyList<TriggerAlert> MatchTriggers(IReadOnlyList<string> lines)
        {
            TriggerMatches++;
            return Alerts;
        }

        public void RaiseBatch(bool live)
        {
            BatchProcessed?.Invoke(new LogSessionBatch("log", "Que", ["line"], live));
        }

        public void RaiseSpell(string spell)
        {
            SpellObserved?.Invoke(spell);
        }

        public void RaiseConsider(string zone)
        {
            LiveMobConsidered?.Invoke(new ConsiderEvent(DateTime.Now, "a bat", "indifferent", "even", 10, false), zone);
        }
    }
}
