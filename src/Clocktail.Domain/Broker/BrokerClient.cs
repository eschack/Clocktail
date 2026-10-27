using System.IO.Pipes;

namespace Clocktail.Domain.Broker;

/// <summary>
/// The unprivileged side: connect to the broker's pipe, send one request, read one response. A null return
/// means the broker is not reachable (not installed, not started), which is how the CLI learns to fall back to
/// running the op itself if it happens to be elevated, or to telling the user the broker is needed.
/// </summary>
public static class BrokerClient
{
    /// <summary>A cancellable, bounded exchange for interactive read-only clients. Cancellation closes the pipe.</summary>
    public static async Task<BrokerResponse?> TryCallAsync(BrokerRequest request, CancellationToken cancellationToken,
        string pipeName = BrokerTransport.PipeName, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(1500, deadline.Token).ConfigureAwait(false);
            using var cancel = deadline.Token.Register(() => pipe.Dispose());
            return await Task.Run(() =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                BrokerTransport.WriteRequest(pipe, request);
                return BrokerTransport.ReadResponse(pipe);
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
        catch (Exception e) when (deadline.IsCancellationRequested && e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The broker status request timed out.", e);
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// How long an answer may take once the request is sent: a write waits up to the window's 30 s for another one, and a
    /// call stuck in a hung driver waits out its TDR delay (30 s on the reference machine) before it fails. Past this the
    /// broker is taken for stuck and the caller is told so (<see cref="TimeoutException"/>), never left waiting forever.
    /// </summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(120);

    /// <exception cref="TimeoutException">The request went out and no answer came within <paramref name="answerWait"/>
    /// (default <see cref="AnswerWait"/>): whether it was carried out is unknown.</exception>
    /// <exception cref="IOException">The pipe broke after the request went out: whether it was carried out is unknown.</exception>
    public static BrokerResponse? TryCall(BrokerRequest req, string pipeName = BrokerTransport.PipeName, int connectMs = 2000, TimeSpan? answerWait = null)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            try { pipe.Connect(connectMs); }
            catch (TimeoutException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            // The write is under the same clock: a pipe's flush waits until the other end has read the request, so a
            // broker stuck before reading would hold the writer as long as one stuck before answering.
            var wait = answerWait ?? AnswerWait;
            var exchange = Task.Run(() =>
            {
                BrokerTransport.WriteRequest(pipe, req);
                return BrokerTransport.ReadResponse(pipe);
            });
            if (!((IAsyncResult)exchange).AsyncWaitHandle.WaitOne(wait))
            {
                _ = exchange.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);   // it ends when the pipe closes
                throw new TimeoutException($"the Clocktail service took longer than {wait.TotalSeconds:0} s to answer");
            }
            return exchange.GetAwaiter().GetResult();
        }
        finally { pipe.Dispose(); }
    }

    /// <summary>Is a broker up and answering on this machine? A short-timeout ping.</summary>
    public static bool IsPresent(string pipeName = BrokerTransport.PipeName)
        => TryCall(new BrokerRequest(BrokerRequest.OpPing), pipeName, connectMs: 500) is { Ok: true };
}
