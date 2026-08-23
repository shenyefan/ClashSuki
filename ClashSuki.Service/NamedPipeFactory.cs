using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClashSuki.Service;

internal static class NamedPipeFactory
{
    /// <summary>
    /// MSIX 服务允许本机已认证用户连接；便携服务只允许安装时登记的所有者连接。
    /// </summary>
    public static NamedPipeServerStream CreateServer(ServiceRuntimeContext runtimeContext)
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        var clientSid = runtimeContext.IsPortable
            ? new SecurityIdentifier(runtimeContext.PortableRegistration!.OwnerSid)
            : new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            clientSid,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            runtimeContext.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity);
    }
}
