using ClashSuki.Utilities;

namespace ClashSuki;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (!SingleInstanceManager.TryAcquirePrimary())
        {
            SingleInstanceManager.RequestActivatePrimary();
            return 0;
        }

        SingleInstanceManager.StartListening();

        try
        {
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        finally
        {
            SingleInstanceManager.ReleasePrimary();
        }

        return 0;
    }
}
