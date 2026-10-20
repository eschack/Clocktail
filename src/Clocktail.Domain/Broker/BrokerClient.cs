using System.IO.Pipes;

namespace Clocktail.Domain.Broker;

/// <summary>
/// The unprivileged side: connect to the broker's pipe, send one request, read one response. A null return
/// means the broker is not reachable (not installed, not started), which is how the CLI learns to fall back to
/// running the op itself if it happens to be elevated, or to telling the user the broker is needed.
/// </summary>
public static class BrokerClient
{
    public static BrokerResponse? TryCall(BrokerRequest req, string pipeName = BrokerTransport.PipeName, int connectMs = 2000)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(connectMs); }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        BrokerTransport.WriteRequest(pipe, req);
        return BrokerTransport.ReadResponse(pipe);
    }

    /// <summary>Is a broker up and answering on this machine? A short-timeout ping.</summary>
    public static bool IsPresent(string pipeName = BrokerTransport.PipeName)
        => TryCall(new BrokerRequest(BrokerRequest.OpPing), pipeName, connectMs: 500) is { Ok: true };
}
