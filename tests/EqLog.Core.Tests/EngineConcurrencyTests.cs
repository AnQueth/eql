using System.Collections;
using EqLog.Core.Tracking;

namespace EqLog.Core.Tests;

public class EngineConcurrencyTests
{
    [Fact]
    public async Task ProcessLines_AllowsSnapshotsAndControlsBetweenLines()
    {
        var engine = new LogEngine { CharacterName = "Quethrosar" };
        using var resume = new ManualResetEventSlim();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = new PausedBatch(
            "[Wed Oct 21 18:00:00 2026] You slash Guardian Golem for 10 points of damage.",
            "[Wed Oct 21 18:00:01 2026] You slash Guardian Golem for 20 points of damage.",
            paused, resume);
        var processing = Task.Run(() => engine.ProcessLines(batch));
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var snapshot = await Task.Run(() =>
            {
                var partial = engine.Snapshot(new DateTime(2026, 10, 21, 18, 0, 0));
                engine.SetSpellDurationOverride("Allure X", 120);
                return partial;
            }).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, snapshot.LinesProcessed);
            Assert.Equal(10, snapshot.SessionDamageByYou);
        }
        finally
        {
            resume.Set();
            await processing.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var complete = engine.Snapshot(new DateTime(2026, 10, 21, 18, 0, 1));
        Assert.Equal(2, complete.LinesProcessed);
        Assert.Equal(30, complete.SessionDamageByYou);
        Assert.Equal(30, Assert.IsType<FightSnapshot>(complete.CurrentFight).Total);
        Assert.Contains(engine.SpellDurations(), spell => spell.Spell == "Allure X" && spell.OverrideSeconds == 120);
    }

    private sealed class PausedBatch(string first, string second, TaskCompletionSource paused,
        ManualResetEventSlim resume) : IReadOnlyList<string>
    {
        public int Count
        {
            get
            {
                return 2;
            }
        }

        public string this[int index]
        {
            get
            {
                return index switch
                {
                    0 => first,
                    1 => second,
                    _ => throw new ArgumentOutOfRangeException(nameof(index)),
                };
            }
        }

        public IEnumerator<string> GetEnumerator()
        {
            yield return first;
            paused.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release the paused log batch.");
            }
            yield return second;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
