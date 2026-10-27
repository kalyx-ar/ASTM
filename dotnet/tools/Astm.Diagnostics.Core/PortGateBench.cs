using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Astm.Abstractions.Transport;
using Astm.Transport;
using PortGate.Client;

namespace Astm.Diagnostics;

/// <summary>Owns a temporary local PortGate and its simulated serial peer. Never installs a service or an OS driver.</summary>
public static class PortGateBench
{
    public static async Task<CommunicationScenarioResult> RunAsync(string executable, CommunicationScenario scenario,
        DiagnosticProfile profile, CancellationToken token, SerialSettings? serial = null)
    {
        token.ThrowIfCancellationRequested();
        serial ??= profile.AstmProfile?.Transport?.Serial?.Default ?? new();
        var state = Path.Combine(Path.GetTempPath(), "astm-portgate-bench-" + Guid.NewGuid().ToString("N"));
        var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "run", "--state", state, "--admin", "127.0.0.1:0", "--name", "test-agent", "--quiet" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("PORTGATE-BENCH-NO-PROCESS");
        var errors = DrainAsync(process.StandardError);
        try
        {
            var ready = (await process.StandardOutput.ReadLineAsync(token))?.Split(' ');
            if (ready is not ["PORTGATE-ADMIN", var adminPort, _]) throw new IOException("PORTGATE-BENCH-START-FAILED");
            using var peerListener = new TcpListener(IPAddress.Loopback, 0); peerListener.Start();
            var accepting = peerListener.AcceptTcpClientAsync(token).AsTask();
            await using var admin = await PortGateAdminClient.ConnectAsync("127.0.0.1", int.Parse(adminPort), "test-bench", cancellationToken: token);
            var configuration = new PortGateConfiguration("test-agent", [new("scenario", "scenario",
                PortGateDevice.TestSerial(((IPEndPoint)peerListener.LocalEndpoint).Port, serial), [new("scenario", 10)])])
                { ListenHost = "127.0.0.1", PlatformProfile = "test" };
            var linkPort = await admin.SetConfigAsync(1, configuration, token);
            using var peer = await accepting.WaitAsync(token); peer.NoDelay = true;
            await using var host = await new PortGateTransportProvider().OpenAsync(new PortGateTarget("scenario", new("127.0.0.1", linkPort), "scenario", "scenario")
                { Settings = serial }, token);
            return await scenario.RunAsync(profile, host, peer.GetStream(), true, token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await errors;
            // Delete only the state directory created by this bench, never a caller-supplied directory.
            var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(Path.GetFullPath(state)) == root && Path.GetFileName(state).StartsWith("astm-portgate-bench-", StringComparison.Ordinal))
                try { if (Directory.Exists(state)) Directory.Delete(state, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    private static async Task DrainAsync(StreamReader reader) { while (await reader.ReadLineAsync() is not null) { } }
}
