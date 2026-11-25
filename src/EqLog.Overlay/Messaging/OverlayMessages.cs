using EqLog.Application.Sessions;
using EqLog.Core;
using EqLog.Core.Parsing;
using EqLog.Core.Triggers;

namespace EqLog.Overlay.Messaging;

/// <summary>Receives one message type from the <see cref="OverlayMessagePump"/>.</summary>
/// <typeparam name="TMessage">The message type.</typeparam>
public interface IMessageReceiver<in TMessage>
{
    /// <summary>Handles a message. Dispatcher messages arrive on the UI thread; background messages on the log reader thread.</summary>
    void Receive(TMessage message);
}

/// <summary>A receiver of render frames that the pump only feeds while it is active and whose data needs shape the capture.</summary>
public interface IOverlayFrameReceiver : IMessageReceiver<OverlayFrameMessage>
{
    /// <summary>Whether the receiver currently wants frames (for example, its overlay is showing).</summary>
    bool WantsFrames
    {
        get;
    }

    /// <summary>Optional frame data this receiver needs while it wants frames.</summary>
    FrameRequirements Requirements
    {
        get;
    }
}

/// <summary>Optional, comparatively expensive data a frame capture includes only when an active receiver needs it.</summary>
public sealed record FrameRequirements(IReadOnlySet<ChatChannel>? ChatChannels = null, bool Spawns = false, bool SpellDurations = false)
{
    /// <summary>No optional data.</summary>
    public static FrameRequirements None { get; } = new();

    /// <summary>Whether chat should be captured.</summary>
    public bool Chat
    {
        get
        {
            return ChatChannels is not null;
        }
    }

    /// <summary>The union of two requirement sets.</summary>
    public FrameRequirements Combine(FrameRequirements other)
    {
        ArgumentNullException.ThrowIfNull(other);
        IReadOnlySet<ChatChannel>? channels = ChatChannels;
        if (other.ChatChannels is { } more)
        {
            channels = channels is null ? more : new HashSet<ChatChannel>(channels.Concat(more));
        }
        return new(channels, Spawns || other.Spawns, SpellDurations || other.SpellDurations);
    }
}

/// <summary>Published on the UI thread at the start of every pump cycle, before the frame is captured.</summary>
public sealed record OverlayTickMessage(DateTime Now, TailProgress Progress);

/// <summary>A captured render frame, published on the UI thread to active frame receivers in subscription order.</summary>
public sealed record OverlayFrameMessage(LogSessionFrame Frame, DateTime Now, TailProgress Progress)
{
    /// <summary>The engine snapshot.</summary>
    public EngineSnapshot Snapshot
    {
        get
        {
            return Frame.Snapshot;
        }
    }

    /// <summary>Detached query results captured with the snapshot.</summary>
    public EngineQuerySnapshot Queries
    {
        get
        {
            return Frame.Queries;
        }
    }

    /// <summary>Recent chat, when an active receiver asked for it.</summary>
    public IReadOnlyList<ChatEvent> Chat
    {
        get
        {
            return Frame.Chat;
        }
    }

    /// <summary>Whether the reader is still replaying history.</summary>
    public bool Replay
    {
        get
        {
            return Frame.Replay;
        }
    }
}

/// <summary>Published on the UI thread after every receiver has handled a frame.</summary>
public sealed record OverlayFrameCompletedMessage(OverlayFrameMessage Frame, Task? ReaderCompletion);

/// <summary>Trigger alerts matched from live log lines (or a manual test), published on the UI thread.</summary>
public sealed record TriggerAlertsMessage(IReadOnlyList<TriggerAlert> Alerts);

/// <summary>A live /consider of a mob, published on the UI thread.</summary>
public sealed record MobConsideredMessage(ConsiderEvent Consider, string Zone);

/// <summary>A live consider assessment, published on the UI thread.</summary>
public sealed record ConsiderAssessmentMessage(ConsiderAssessmentEvent Assessment);

/// <summary>A processed batch of log lines, delivered synchronously on the log reader thread.</summary>
public sealed record LogBatchMessage(LogSessionBatch Batch);

/// <summary>A spell name seen in the log, delivered synchronously on the log reader thread.</summary>
public sealed record SpellObservedMessage(string Spell);
