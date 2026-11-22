using System.Runtime.InteropServices;
using Astm.Service;
using Astm.Service.Host;
using System.IO.Pipes;
using System.Net.Sockets;

if (args.Length == 2 && args[0] == "--stop")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        Stream connection;
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token); connection = pipe;
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(args[1]), timeout.Token); connection = new NetworkStream(socket, ownsSocket: true);
        }
        await using (connection)
        {
            var id = Guid.CreateVersion7();
            await IpcFrames.WriteAsync(connection, new(1, id, "request", ServiceJson.Element(new Ok())) { Method = "service.shutdown" }, 4096, timeout.Token);
            var reply = await IpcFrames.ReadAsync(connection, 4096, timeout.Token);
            return reply is { Kind: "result" } && reply.Id == id ? 0 : 1;
        }
    }
    catch (Exception) { Console.Error.WriteLine("ASTM-SERVICE-STOP-FAILED"); return 1; }
}

if (args.Length != 2 || args[0] != "--config")
{
    Console.Error.WriteLine("Uso: astm-service --config <configuracion.json> | --stop <endpoint>");
    return 2;
}
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var term = !OperatingSystem.IsWindows() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{ context.Cancel = true; stop.Cancel(); }) : null;
try
{
    // The same file receives the authenticated IPC changes (atomic replace), when `administration` is configured.
    var store = new FileServiceConfigurationStore(args[1]);
    var configuration = store.Load();
    await using var service = new CommunicationService(configuration, BundledServiceProfiles.Create(), store: store);
    using var shutdown = service.Stopping.Register(stop.Cancel);
    await using var listener = new LocalIpcListener(configuration.Endpoint, configuration.OwnershipDirectory);
    var clients = new List<Task>();
    using var slots = new SemaphoreSlim(8);
    try
    {
        while (!stop.IsCancellationRequested)
        {
            await slots.WaitAsync(stop.Token);
            Stream stream;
            try { stream = await listener.AcceptAsync(stop.Token); }
            catch { slots.Release(); throw; }
            clients.RemoveAll(task => task.IsCompleted);
            clients.Add(Task.Run(async () =>
            {
                try { await service.ServeAsync(stream, stop.Token); }
                catch (Exception) { /* No payloads, paths or exception messages on generic logs. */ }
                finally { await stream.DisposeAsync(); slots.Release(); }
            }, CancellationToken.None));
        }
    }
    finally { stop.Cancel(); await Task.WhenAll(clients); }
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (Exception exception)
{
    Console.Error.WriteLine(exception is ServiceException known ? known.Code : "ASTM-SERVICE-" + exception.GetType().Name);
    return 1;
}
