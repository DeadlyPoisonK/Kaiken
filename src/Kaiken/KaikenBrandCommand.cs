using System;
using System.Net.Sockets;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Botón del ícono de Kaiken: prueba la conexión del puente en vivo (el que usa Claude vía MCP).
/// Muestra la versión, lo que Kaiken lee de settings.json y si el puerto responde; si el puente
/// no está activo, ofrece iniciarlo en ese momento.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class KaikenBrandCommand : IExternalCommand
{
    private const string Title = "Kaiken";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var settings = KaikenSettings.Current.LiveBridge;
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        bool listening = IsListening(settings.Port);

        string info = $"Kaiken {version}\n\n" +
                      $"settings.json: puente {(settings.Enabled ? "activado" : "DESACTIVADO")}, puerto {settings.Port}\n" +
                      $"Puerto {settings.Port}: {(listening ? "responde (puente en vivo activo)" : "no responde")}";

        if (listening)
        {
            TaskDialog.Show(Title, info);
            return Result.Succeeded;
        }

        var dlg = new TaskDialog(Title)
        {
            MainInstruction = "El puente en vivo no está activo",
            MainContent = info,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Iniciar el puente ahora",
            $"Abre el puerto {settings.Port} en esta sesión de Revit (no cambia settings.json).");
        if (dlg.Show() != TaskDialogResult.CommandLink1) return Result.Succeeded;

        string? error = App.Instance?.StartBridge(settings.Port);
        bool ok = error == null && IsListening(settings.Port);
        App.BridgeLog(ok ? $"puente iniciado a mano en {settings.Port}" : $"inicio manual falló: {error}");
        TaskDialog.Show(Title, ok
            ? $"Puente iniciado en 127.0.0.1:{settings.Port}."
            : $"No se pudo iniciar el puente:\n{error ?? "el puerto no responde"}");
        return Result.Succeeded;
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(500) && client.Connected;
        }
        catch { return false; }
    }
}
