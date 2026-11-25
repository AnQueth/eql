using System.Runtime.ExceptionServices;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class WhatsNewWindowTests
{
    [Fact]
    public void Window_LoadsBundledDocumentAndCloseCommand()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new WhatsNewWindow();
                bool closed = false;
                window.Closed += (_, _) =>
                {
                    closed = true;
                };
                window.Show();
                var root = Assert.IsType<DockPanel>(window.Content);
                var viewer = Assert.Single(root.Children.OfType<FlowDocumentScrollViewer>());
                Assert.NotNull(viewer.Document);
                Assert.NotEmpty(viewer.Document.Blocks);
                Assert.Contains(window.InputBindings.OfType<KeyBinding>(),
                    binding => binding.Key == Key.Escape && binding.Command == ApplicationCommands.Close);
                var close = Assert.Single(root.Children.OfType<Button>());
                Assert.Equal(ApplicationCommands.Close, close.Command);
                var peer = new ButtonAutomationPeer(close);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () =>
                {
                });
                Assert.True(closed);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
