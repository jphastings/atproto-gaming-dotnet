using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ByJP.AtprotoGaming.Core.Adapters;

namespace ByJP.AtprotoGaming.Sidecar;

/// <summary>
/// The loopback TCP listener that frames the wire protocol: one UTF-8 JSON object per
/// line (<c>\n</c>-delimited), request → response in order. Each connection gets its
/// own <see cref="Connection"/> state; commands are handed to a shared
/// <see cref="CommandProcessor"/>. Binds <see cref="IPAddress.Loopback"/> only.
/// </summary>
internal sealed class WireServer
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly int _port;
    private readonly CommandProcessor _processor;
    private readonly ILogSink _log;

    public WireServer(int port, CommandProcessor processor, ILogSink log)
    {
        _port = port;
        _processor = processor;
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellation)
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _log.Error($"could not bind 127.0.0.1:{_port} ({ex.Message}). " +
                       "Is another sidecar already running? Stop it, or change 'port' in config.json.");
            return;
        }
        _log.Info($"wire protocol listening on 127.0.0.1:{_port}");
        cancellation.Register(listener.Stop);

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (cancellation.IsCancellationRequested) { break; }

                _ = HandleClientAsync(client, cancellation);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellation)
    {
        using (client)
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
            {
                _log.Warn("refused a non-loopback connection");
                return;
            }

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Utf8);
                using var writer = new StreamWriter(stream, Utf8) { NewLine = "\n", AutoFlush = true };

                var connection = new Connection();
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (cancellation.IsCancellationRequested) break;
                    if (line.Length == 0) continue;

                    var result = await _processor.ProcessAsync(connection, line).ConfigureAwait(false);
                    await writer.WriteLineAsync(result.Response.ToJsonString()).ConfigureAwait(false);
                    if (result.CloseAfter) break;
                }
            }
            catch (IOException) { /* client vanished mid-stream — normal */ }
            catch (System.Exception ex) { _log.Error("connection handler failed", ex); }
        }
    }
}
