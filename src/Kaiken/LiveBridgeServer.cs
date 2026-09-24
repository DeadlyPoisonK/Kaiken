using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Servidor TCP local (127.0.0.1, puerto 5551 por defecto, nunca expuesto a la red). Se eligió socket
/// crudo en vez de HttpListener porque HttpListener exige permisos de administrador
/// (o una reserva de URL vía netsh) para registrar el prefijo, incluso en loopback.
/// Protocolo: una línea de JSON de entrada {"op": "...", "args": {...}}, una línea
/// de JSON de salida, y se cierra la conexión.
/// </summary>
public class LiveBridgeServer
{
    private readonly int _port;

    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _running;
    private readonly ExternalEvent _externalEvent;
    private readonly LiveBridgeEventHandler _handler;

    public LiveBridgeServer(ExternalEvent externalEvent, LiveBridgeEventHandler handler, int port)
    {
        _port = port;
        _externalEvent = externalEvent;
        _handler = handler;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        _acceptThread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { /* ya estaba detenido */ }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch
            {
                break; // el listener se detuvo (Stop())
            }

            ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            string requestLine;
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true))
            {
                requestLine = reader.ReadLine() ?? "";
            }

            string responseLine;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(requestLine) ? "{}" : requestLine);
                string op = doc.RootElement.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";
                JsonElement args = doc.RootElement.TryGetProperty("args", out var a) ? a : default;

                var pending = new PendingRequest { Operation = op, Args = args };
                _handler.Enqueue(pending);
                _externalEvent.Raise();

                // test_reopen abre modelos de nube completos por API — puede
                // tardar varios minutos (visto en vivo: CEC y ATR tardaron
                // más de 30s solo en abrir). El resto de operaciones son
                // rápidas, se quedan con el timeout corto de siempre.
                var timeout = op == "test_reopen" ? TimeSpan.FromMinutes(8) : TimeSpan.FromSeconds(30);
                bool completed = pending.Done.Wait(timeout);
                if (!completed)
                    responseLine = "{\"error\":\"timeout esperando a Revit (¿hay un documento abierto?)\"}";
                else if (pending.Error != null)
                    responseLine = JsonSerializer.Serialize(new { error = pending.Error });
                else
                    responseLine = pending.ResultJson ?? "{}";
            }
            catch (Exception ex)
            {
                responseLine = JsonSerializer.Serialize(new { error = ex.Message });
            }

            byte[] bytes = Encoding.UTF8.GetBytes(responseLine + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }
}
