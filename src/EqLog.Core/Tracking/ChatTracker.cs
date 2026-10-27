using EqLog.Core.Parsing;

namespace EqLog.Core.Tracking;

/// <summary>Keeps a rolling buffer of chat messages.</summary>
public sealed class ChatTracker
{
    private readonly LinkedList<ChatEvent> _messages = new();

    public int Capacity { get; set; } = 300;

    /// <summary>Incremented whenever a message is added, so views can skip redraws.</summary>
    public long Version { get; private set; }

    public void OnChat(ChatEvent e)
    {
        _messages.AddLast(e);
        while (_messages.Count > Capacity)
        {
            _messages.RemoveFirst();
        }

        Version++;
    }

    public IReadOnlyList<ChatEvent> Recent(int count, ISet<ChatChannel>? channels)
    {
        var result = new List<ChatEvent>(count);
        for (var node = _messages.Last; node is not null && result.Count < count; node = node.Previous)
        {
            if (channels is null || channels.Contains(node.Value.Channel))
            {
                result.Add(node.Value);
            }
        }

        result.Reverse();
        return result;
    }
}
