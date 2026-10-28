namespace EqLog.Core;

/// <summary>A point-in-time view of a wiki HTTP request, including retry backoff.</summary>
public sealed record WikiRequestActivity(long Id, string Name, string Url, WikiPriority Priority,
    string State, int Attempt, DateTime StartedUtc);

/// <summary>A recent HTTP outcome or repository processing error.</summary>
public sealed record WikiActivityEvent(DateTime TimeUtc, string Name, string Message, bool IsError);

/// <summary>Thread-safe copies of live wiki work and bounded recent outcomes.</summary>
public sealed record WikiActivitySnapshot(IReadOnlyList<WikiRequestActivity> Requests,
    IReadOnlyList<WikiActivityEvent> Recent, IReadOnlyList<string> Operations);
