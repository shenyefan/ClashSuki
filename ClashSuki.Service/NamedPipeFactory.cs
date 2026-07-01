using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClashSuki.Service;

internal static class NamedPipeFactory
{
    /// <summary>
    /// LocalSystem 服务创建的管道默认拒绝普通用户连接，必须显式授予 Authenticated Users 读写权限。
    /// </summary>
    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity);
    }
}
