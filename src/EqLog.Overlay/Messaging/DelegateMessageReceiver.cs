namespace EqLog.Overlay.Messaging;

/// <summary>Adapts a delegate to <see cref="IMessageReceiver{TMessage}"/> for shell-level subscriptions.</summary>
/// <typeparam name="TMessage">The message type.</typeparam>
public sealed class DelegateMessageReceiver<TMessage> : IMessageReceiver<TMessage>
{
    private readonly Action<TMessage> _receive;

    /// <summary>Creates a receiver that calls <paramref name="receive"/> for each message.</summary>
    public DelegateMessageReceiver(Action<TMessage> receive)
    {
        ArgumentNullException.ThrowIfNull(receive);
        _receive = receive;
    }

    /// <inheritdoc />
    public void Receive(TMessage message)
    {
        _receive(message);
    }
}

/// <summary>Convenience subscriptions for delegates.</summary>
public static class OverlayMessagePumpExtensions
{
    /// <summary>Subscribes <paramref name="receive"/> to UI-thread messages of type <typeparamref name="TMessage"/>.</summary>
    public static IDisposable Subscribe<TMessage>(this OverlayMessagePump pump, Action<TMessage> receive)
    {
        ArgumentNullException.ThrowIfNull(pump);
        return pump.Subscribe(new DelegateMessageReceiver<TMessage>(receive));
    }

    /// <summary>Subscribes <paramref name="receive"/> to reader-thread messages of type <typeparamref name="TMessage"/>.</summary>
    public static IDisposable SubscribeBackground<TMessage>(this OverlayMessagePump pump, Action<TMessage> receive)
    {
        ArgumentNullException.ThrowIfNull(pump);
        return pump.SubscribeBackground(new DelegateMessageReceiver<TMessage>(receive));
    }
}
