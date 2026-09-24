using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Handler de ExternalEvent: procesa las peticiones encoladas por el servidor
/// (socket) en el hilo principal de Revit, que es el único hilo desde el que
/// se puede llamar a la Revit API.
/// </summary>
public class LiveBridgeEventHandler : IExternalEventHandler
{
    private readonly ConcurrentQueue<PendingRequest> _queue = new();

    public void Enqueue(PendingRequest request) => _queue.Enqueue(request);

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var request))
        {
            try
            {
                request.ResultJson = LiveBridgeOperations.Dispatch(app, request.Operation, request.Args);
            }
            catch (Exception ex)
            {
                request.Error = ex.Message;
            }
            finally
            {
                request.Done.Set();
            }
        }
    }

    public string GetName() => "Kaiken Live Bridge";
}
