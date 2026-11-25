using EqLog.Application.Sessions;
using EqLog.Core.Parsing;
using EqLog.Core.Triggers;

namespace EqLog.Overlay.Messaging;

/// <summary>The log reader and session surface the <see cref="OverlayMessagePump"/> listens to and captures frames from.</summary>
public interface IOverlayLogFeed
{
    /// <summary>A processed batch, raised on the reader thread.</summary>
    event Action<LogSessionBatch>? BatchProcessed;

    /// <summary>A live /consider, raised on the reader thread.</summary>
    event Action<ConsiderEvent, string>? LiveMobConsidered;

    /// <summary>A live consider assessment, raised on the reader thread.</summary>
    event Action<ConsiderAssessmentEvent>? LiveConsiderAssessment;

    /// <summary>A spell name seen in the log, raised on the reader thread.</summary>
    event Action<string>? SpellObserved;

    /// <summary>Current reader progress.</summary>
    TailProgress Progress
    {
        get;
    }

    /// <summary>The running reader, if any.</summary>
    Task? Completion
    {
        get;
    }

    /// <summary>Changes whenever a different log is started; frames captured across a change are discarded.</summary>
    long Revision
    {
        get;
    }

    /// <summary>Captures a frame including the optional data in <paramref name="requirements"/>.</summary>
    Task<LogSessionFrame> CaptureFrameAsync(FrameRequirements requirements);

    /// <summary>Matches live lines against the configured alert triggers.</summary>
    IReadOnlyList<TriggerAlert> MatchTriggers(IReadOnlyList<string> lines);
}

/// <summary>Adapts the application's <see cref="LogSessionService"/> and <see cref="GameSession"/> to <see cref="IOverlayLogFeed"/>.</summary>
public sealed class SessionLogFeed : IOverlayLogFeed
{
    private readonly LogSessionService _source;
    private readonly GameSession _session;

    /// <summary>Creates the adapter.</summary>
    public SessionLogFeed(LogSessionService source, GameSession session)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(session);
        _source = source;
        _session = session;
    }

    /// <inheritdoc />
    public event Action<LogSessionBatch>? BatchProcessed
    {
        add
        {
            _source.BatchProcessed += value;
        }
        remove
        {
            _source.BatchProcessed -= value;
        }
    }

    /// <inheritdoc />
    public event Action<ConsiderEvent, string>? LiveMobConsidered
    {
        add
        {
            _source.LiveMobConsidered += value;
        }
        remove
        {
            _source.LiveMobConsidered -= value;
        }
    }

    /// <inheritdoc />
    public event Action<ConsiderAssessmentEvent>? LiveConsiderAssessment
    {
        add
        {
            _source.LiveConsiderAssessment += value;
        }
        remove
        {
            _source.LiveConsiderAssessment -= value;
        }
    }

    /// <inheritdoc />
    public event Action<string>? SpellObserved
    {
        add
        {
            _source.SpellObserved += value;
        }
        remove
        {
            _source.SpellObserved -= value;
        }
    }

    /// <inheritdoc />
    public TailProgress Progress
    {
        get
        {
            return _source.Progress;
        }
    }

    /// <inheritdoc />
    public Task? Completion
    {
        get
        {
            return _source.Completion;
        }
    }

    /// <inheritdoc />
    public long Revision
    {
        get
        {
            return _session.Revision;
        }
    }

    /// <inheritdoc />
    public Task<LogSessionFrame> CaptureFrameAsync(FrameRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var channels = requirements.ChatChannels is { } selected
            ? new HashSet<ChatChannel>(selected)
            : [];
        return _session.CaptureFrameAsync(_source, requirements.Chat, channels,
            includeSpawns: requirements.Spawns, includeSpellDurations: requirements.SpellDurations);
    }

    /// <inheritdoc />
    public IReadOnlyList<TriggerAlert> MatchTriggers(IReadOnlyList<string> lines)
    {
        return _session.MatchTriggers(lines);
    }
}
