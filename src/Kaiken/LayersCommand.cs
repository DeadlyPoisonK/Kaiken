using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Panel acoplable "Capas". Revit solo deja registrar paneles en OnStartup: aquí se crea el panel
/// (que se suscribe a los eventos de vista y documento) y el botón de la cinta solo lo muestra u oculta.
/// </summary>
internal sealed class LayersPane : IDockablePaneProvider
{
    internal static readonly DockablePaneId PaneId = new(new Guid("3d8e5a17-9c42-4b6f-a1e0-7f2b4c9d6e53"));

    private readonly LayersPanel _panel;

    private LayersPane(LayersPanel panel) => _panel = panel;

    /// <summary>Registra el panel. Nunca debe tumbar la carga del add-in.</summary>
    internal static void Register(UIControlledApplication application)
    {
        try
        {
            application.RegisterDockablePane(PaneId, "Capas", new LayersPane(new LayersPanel(application)));
        }
        catch (Exception ex)
        {
            App.BridgeLog($"capas: no se pudo registrar el panel: {ex}");
        }
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _panel;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right, MinimumWidth = 260 };
        data.VisibleByDefault = false;
    }
}

/// <summary>Botón Capas: muestra u oculta el panel acoplable.</summary>
[Transaction(TransactionMode.ReadOnly)]
public class LayersCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            DockablePane pane = commandData.Application.GetDockablePane(LayersPane.PaneId);
            if (pane.IsShown()) pane.Hide();
            else pane.Show();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
