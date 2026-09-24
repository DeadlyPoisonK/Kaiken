using System.Text.Json;
using System.Threading;

namespace Kaiken;

/// <summary>
/// Una petición encolada desde el hilo del socket, esperando a ser procesada
/// en el hilo principal de Revit (vía ExternalEvent).
/// </summary>
public class PendingRequest
{
    public string Operation = "";
    public JsonElement Args;
    public string? ResultJson;
    public string? Error;
    public ManualResetEventSlim Done { get; } = new(false);
}
