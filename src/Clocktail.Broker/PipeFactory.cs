using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Clocktail.Broker;

/// <summary>
/// The pipe's security is the privilege boundary made explicit. The broker runs as the user, so only that same
/// user needs to reach it; LocalSystem is allowed so the OS and the broker's own tooling can; the network is
/// denied outright, because a named pipe can be opened remotely and this one never should be. This is the only
/// part of the serve loop that is Windows-specific, which is why the loop takes the stream from here as a factory.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PipeFactory
{
    public static NamedPipeServerStream Create(string pipeName)
    {
        var user = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("cannot resolve the broker's own user SID for the pipe ACL");

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, security);
    }
}
