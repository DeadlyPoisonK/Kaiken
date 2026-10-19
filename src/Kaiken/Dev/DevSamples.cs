using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken.Dev;

// Código de prueba del modo desarrollo (ver DevMode.cs). La carpeta Dev\ solo se compila en los
// builds de tools\dev-build.ps1, nunca en el add-in instalado ni en el instalador. Lo que se
// prueba aquí y sirve, se mueve después a su archivo normal.

/// <summary>Comando de prueba: confirma que el botón Dev ejecuta código del build nuevo.</summary>
[Transaction(TransactionMode.Manual)]
public class DevSampleCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument? uidoc = commandData.Application.ActiveUIDocument;
        TaskDialog.Show("Kaiken Dev",
            $"Comando de prueba desde {typeof(DevSampleCommand).Assembly.GetName().Name}\n\n" +
            $"Documento: {uidoc?.Document.Title ?? "(ninguno)"}\n" +
            $"Vista activa: {uidoc?.ActiveView?.Name ?? "(ninguna)"}\n" +
            $"App.Instance en esta DLL: {(App.Instance == null ? "null (esperado: OnStartup no corre en la DLL dev)" : "asignado")}");
        return Result.Succeeded;
    }
}

/// <summary>Métodos estáticos para la operación dev_invoke del puente en vivo.</summary>
public static class DevSamples
{
    /// <summary>Qué build responde y sobre qué documento: sirve para comprobar que una recarga tomó el build nuevo.</summary>
    public static object Info(UIApplication app) => new
    {
        assembly = typeof(DevSamples).Assembly.GetName().Name,
        document = app.ActiveUIDocument?.Document.Title,
        activeView = app.ActiveUIDocument?.ActiveView?.Name,
        sheets = app.ActiveUIDocument == null ? 0
            : new FilteredElementCollector(app.ActiveUIDocument.Document).OfClass(typeof(ViewSheet)).GetElementCount(),
    };

    /// <summary>
    /// Posición de los viewports en las láminas, en mm de papel (el puente normal no la expone).
    /// args: { "sheetNumber": "..." } opcional; sin él, todas las láminas.
    /// </summary>
    public static object ViewportPositions(Document doc, JsonElement args)
    {
        string? number = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("sheetNumber", out var n) ? n.GetString() : null;
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder && (number == null || s.SheetNumber == number))
            .OrderBy(s => s.SheetNumber)
            .ToList();

        static double Mm(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 2);

        return new
        {
            count = sheets.Count,
            sheets = sheets.Select(s => new
            {
                sheetNumber = s.SheetNumber,
                name = s.Name,
                viewports = s.GetAllViewports().Select(id => doc.GetElement(id)).OfType<Viewport>().Select(vp =>
                {
                    XYZ c = vp.GetBoxCenter();
                    Outline box = vp.GetBoxOutline();
                    return new
                    {
                        id = vp.Id.Value,
                        view = doc.GetElement(vp.ViewId)?.Name,
                        centerMm = new[] { Mm(c.X), Mm(c.Y) },
                        minMm = new[] { Mm(box.MinimumPoint.X), Mm(box.MinimumPoint.Y) },
                        maxMm = new[] { Mm(box.MaximumPoint.X), Mm(box.MaximumPoint.Y) },
                        rotation = vp.Rotation.ToString(),
                    };
                }).ToList(),
            }).ToList(),
        };
    }
}

/// <summary>
/// Panel de prueba para el panel acoplable Kaiken Dev. Muestra el patrón para lo que haga cambios
/// en el modelo desde un botón del panel: ExternalEvent creado en el constructor (que corre en
/// contexto de la API) y liberado en Dispose, que Kaiken llama al reemplazar el panel.
/// </summary>
public class DevSamplePanel : UserControl, IDisposable
{
    private readonly CountHandler _handler = new();
    private readonly ExternalEvent _event;

    public DevSamplePanel(UIApplication app)
    {
        _event = ExternalEvent.Create(_handler);

        var result = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        _handler.Output = result;

        var button = new Button { Content = "Contar elementos de la vista activa", Padding = new Thickness(8, 4, 8, 4) };
        button.Click += (_, _) => _event.Raise();

        var stack = new StackPanel { Margin = new Thickness(8) };
        stack.Children.Add(new TextBlock
        {
            Text = $"Panel de prueba de {typeof(DevSamplePanel).Assembly.GetName().Name}",
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        stack.Children.Add(button);
        stack.Children.Add(result);
        Content = stack;
    }

    public void Dispose() => _event.Dispose();

    private sealed class CountHandler : IExternalEventHandler
    {
        public TextBlock? Output;

        public void Execute(UIApplication app)
        {
            View? view = app.ActiveUIDocument?.ActiveView;
            if (Output == null) return;
            Output.Text = view == null
                ? "No hay vista activa."
                : $"{view.Name}: {new FilteredElementCollector(view.Document, view.Id).WhereElementIsNotElementType().GetElementCount()} elementos";
        }

        public string GetName() => "Kaiken Dev: panel de prueba";
    }
}
