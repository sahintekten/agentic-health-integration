using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Adapter;

/// <summary>Accepts HL7 v2 messages pushed over MLLP, stores them and sends an ACK.</summary>
public sealed class MllpListener : BackgroundService
{
    private const byte Start = 0x0B;
    private const byte End = 0x1C;
    private const byte Cr = 0x0D;

    private readonly FhirStore _store;
    private readonly ILogger<MllpListener> _log;
    private readonly int _port;

    public MllpListener(FhirStore store, ILogger<MllpListener> log, IConfiguration config)
    {
        _store = store;
        _log = log;
        _port = int.TryParse(config["MLLP_PORT"], out var p) ? p : 2576;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _log.LogInformation("MLLP listener on port {Port}", _port);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleAsync(client, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                int end;
                while ((end = IndexOfEnd(buffer)) >= 0)
                {
                    var start = buffer.IndexOf(Start);
                    var text = Encoding.UTF8.GetString(buffer.GetRange(start + 1, end - start - 1).ToArray());
                    buffer.RemoveRange(0, end + 2);
                    var record = Hl7Message.Flatten(text);
                    var controlId = record.Get("MSH-10");
                    var ok = controlId is not null;
                    if (ok)
                        _store.AddPushed(record);
                    var ack = Ack(record, ok ? "AA" : "AR");
                    await stream.WriteAsync(new[] { Start }.Concat(Encoding.UTF8.GetBytes(ack)).Concat(new[] { End, Cr }).ToArray(), ct);
                    // only the control id and the result: never log patient data
                    _log.LogInformation("MLLP message {ControlId} -> {Code}", controlId, ok ? "AA" : "AR");
                }
            }
        }
    }

    private static int IndexOfEnd(List<byte> buffer)
    {
        for (var i = 0; i < buffer.Count - 1; i++)
            if (buffer[i] == End && buffer[i + 1] == Cr)
                return i;
        return -1;
    }

    private static string Ack(SourceRecord message, string code)
    {
        var now = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var trigger = message.Get("MSH-9.2") ?? "";
        return $"MSH|^~\\&|FHIR_ADAPTER|HOSP|{message.Get("MSH-3")}|{message.Get("MSH-4")}|{now}||ACK^{trigger}^ACK|ACK{message.Get("MSH-10")}|P|2.5\r" +
               $"MSA|{code}|{message.Get("MSH-10")}";
    }
}
