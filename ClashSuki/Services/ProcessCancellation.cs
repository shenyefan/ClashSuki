using System.Diagnostics;

namespace ClashSuki.Services;

internal static class ProcessCancellation
{
    public static CancellationTokenRegistration TerminateOnCancellation(
        Process process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        return cancellationToken.Register(static state =>
        {
            var childProcess = (Process)state!;
            try
            {
                if (!childProcess.HasExited)
                {
                    childProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process may have exited between HasExited and Kill.
            }
        }, process);
    }
}
