using System.IO.Pipes;

namespace Clocktail.Domain.Broker;

/// <summary>
/// The broker's accept loop. It does not create the pipe itself: the caller passes a factory, because the only
/// part that differs between the real broker and a test is the pipe's security. On Windows the service hands in
/// a stream whose ACL admits just the installing user and LocalSystem; a test hands in a plain stream.
///
/// Every connection is served on its own task, and the next pipe instance is listening before the current one is
/// answered. Serving one at a time looked simpler but made every client hostage to the slowest one: a client that
/// connected and never sent its request (or a write waiting on the driver) left a read-only `ownership` query from
/// the UI hanging with no answer. Concurrency is safe because the dispatcher already serializes what must be
/// serialized (one write window); reads touch only files written atomically. A client gets
/// <see cref="RequestTimeout"/> to send its request, after which the connection is dropped.
/// </summary>
public sealed class BrokerServer(Func<NamedPipeServerStream> listen, BrokerDispatcher dispatcher)
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public async Task RunAsync(CancellationToken ct)
    {
        var inFlight = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = listen();
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) { pipe?.Dispose(); break; }
            catch (IOException) { pipe?.Dispose(); continue; }   // a broken connection is not a reason to stop the broker
            inFlight.RemoveAll(t => t.IsCompleted);
            inFlight.Add(Task.Run(() => Serve(pipe)));
        }
        await Task.WhenAll(inFlight);
    }

    /// <summary>Accept and answer exactly one connection (tests use it to drive the loop one step at a time).</summary>
    public async Task ServeOneAsync(CancellationToken ct)
    {
        var pipe = listen();
        await pipe.WaitForConnectionAsync(ct);
        await Task.Run(() => Serve(pipe), ct);
    }

    private void Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                var read = Task.Run(() => BrokerTransport.ReadRequest(pipe));
                if (!read.Wait(RequestTimeout)) return;   // disposing the pipe ends the stalled read
                BrokerResponse resp;
                try { resp = dispatcher.Dispatch(read.Result); }
                catch (Exception e) { resp = new BrokerResponse(false, e.Message); }   // a dispatch bug answers the client, never kills the broker
                BrokerTransport.WriteResponse(pipe, resp);
            }
            catch (AggregateException e) when (e.InnerException is InvalidDataException bad)
            {
                try { BrokerTransport.WriteResponse(pipe, new BrokerResponse(false, bad.Message)); } catch (IOException) { }
            }
            catch (AggregateException e) when (e.InnerException is IOException or ObjectDisposedException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                if (pipe.IsConnected) try { pipe.Disconnect(); } catch (IOException) { } catch (ObjectDisposedException) { }
            }
        }
    }
}
