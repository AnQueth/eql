using System.Windows.Threading;
using EqLog.Application.Inventory;
using EqLog.Core;

namespace EqLog.Overlay;

/// <summary>Dispatcher-owned latest-request work. Workers receive only detached values.</summary>
internal sealed class InventoryFilterWork
{
    private readonly Dispatcher _dispatcher;
    private readonly object _requestGate = new();
    private readonly SemaphoreSlim _worker = new(1, 1);
    private CancellationTokenSource? _request;
    private long _generation;

    public InventoryFilterWork(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _dispatcher.ShutdownStarted += (_, _) => Cancel();
    }

    public Task Pending { get; private set; } = Task.CompletedTask;

    public void Cancel()
    {
        _dispatcher.VerifyAccess();
        _generation++;
        lock (_requestGate)
        {
            _request?.Cancel();
            _request = null;
        }
    }

    public Task Start<T>(
        Func<CancellationToken, T> query,
        Func<T, CancellationToken, Task> apply,
        Action<Exception> failed,
        bool debounce = true)
    {
        _dispatcher.VerifyAccess();
        Cancel();
        var request = new CancellationTokenSource();
        lock (_requestGate)
        {
            _request = request;
        }
        long generation = _generation;
        Pending = RunAsync();
        return Pending;

        async Task RunAsync()
        {
            try
            {
                if (debounce)
                {
                    await Task.Delay(200, request.Token).ConfigureAwait(false);
                }
                var result = await Task.Run(async () =>
                {
                    await _worker.WaitAsync(request.Token).ConfigureAwait(false);
                    try
                    {
                        request.Token.ThrowIfCancellationRequested();
                        return query(request.Token);
                    }
                    finally
                    {
                        _worker.Release();
                    }
                }, request.Token).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(async () =>
                {
                    // A source refresh, session change, unload, or new text invalidates this result.
                    if (generation != _generation || request.IsCancellationRequested)
                    {
                        return;
                    }
                    await apply(result, request.Token);
                }, DispatcherPriority.Background, request.Token).Task.Unwrap().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                // Expected when newer work or the UI lifetime supersedes this request.
            }
            catch (Exception error)
            {
                // Report even stale worker failures rather than leaving an unobserved task.
                if (_dispatcher.HasShutdownStarted)
                {
                    UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport);
                }
                else
                {
                    try
                    {
                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (generation == _generation)
                            {
                                failed(error);
                            }
                            else
                            {
                                UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport);
                            }
                        });
                    }
                    catch (OperationCanceledException) when (_dispatcher.HasShutdownStarted)
                    {
                        UsageTelemetry.TrackError(error, UsageErrorSource.InventoryImport);
                    }
                }
            }
            finally
            {
                // Cleanup must also work after dispatcher shutdown, without racing Cancel.
                lock (_requestGate)
                {
                    if (ReferenceEquals(_request, request))
                    {
                        _request = null;
                    }
                    request.Dispose();
                }
            }
        }
    }

    internal static int[] MatchNames(string[] names, string filter, CancellationToken cancellation)
    {
        string text = filter.Trim();
        var matches = new List<int>();
        for (int index = 0; index < names.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (names[index].Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(index);
            }
        }
        return matches.ToArray();
    }

    internal static InventoryMergeResult QueryMerge(
        InventoryExportItem[] rows, string filter, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var groups = InventoryQueries.MergeGroups(rows);
        string text = filter.Trim();
        var shown = new List<InventoryMergeDisplay>();
        foreach (var group in groups)
        {
            cancellation.ThrowIfCancellationRequested();
            if (group.BaseName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                group.Items.Any(item => item.Where.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                var table = new List<IReadOnlyList<string>>();
                foreach (var item in group.Items)
                {
                    cancellation.ThrowIfCancellationRequested();
                    table.Add([item.Rank > 0 ? $"+{item.Rank}" : "+0", item.Count.ToString("N0"), item.Where]);
                }
                shown.Add(new(group.BaseName, group.Total, table));
            }
        }
        return new(groups.Count, shown);
    }
}

internal sealed record InventoryMergeDisplay(string BaseName, int Total, IReadOnlyList<IReadOnlyList<string>> Rows);

internal sealed record InventoryMergeResult(int Total, IReadOnlyList<InventoryMergeDisplay> Shown);
