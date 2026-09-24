using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Exporta una vista 3D al formato IFC con la configuración fija del proyecto:
///   · IFC version      : IFC4x3 [Experimental]
///   · Coordinate Base  : Internal Origin
///   · Advanced         : Use Type name only for IFCType name = true
///   · Property Sets    : Revit + IFC common + base quantities
///
/// Si la vista activa es 3D la usa directamente.
/// Si no, muestra un listado de todas las vistas 3D del documento para elegir.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class ExportIfcCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiApp = commandData.Application;
        Document doc = uiApp.ActiveUIDocument.Document;

        // ── 1. Resolver la vista 3D a exportar ─────────────────────────────
        View3D? vista3D = ResolveView3D(doc, uiApp);
        if (vista3D == null) return Result.Cancelled;

        // ── 2. Carpeta de destino ───────────────────────────────────────────
        string folderPath = PromptFolder();
        if (string.IsNullOrEmpty(folderPath)) return Result.Cancelled;

        // ── 3. Nombre del archivo = Title del documento ──────────────────────
        // doc.PathName no es confiable para modelos en la nube (ACC) — puede
        // venir vacío o con un formato que no es una ruta real. doc.Title sí
        // funciona siempre (es el nombre que Revit muestra en la pestaña),
        // y es lo mismo que usa ExportIfcBatchCommand.
        string docName = doc.Title;
        if (string.IsNullOrWhiteSpace(docName))
            docName = "modelo";

        string fileName = ExportHelpers.LimpiarNombre(docName) + ".ifc";

        // ── 4. Construir IFCExportOptions con la configuración requerida ────
        var ifcOptions = ExportHelpers.BuildIfcOptions(vista3D);

        // ── 5. Exportar ─────────────────────────────────────────────────────
        // doc.Export() es síncrono y bloquea el hilo de UI: Revit puede mostrar
        // "(No responde)" en modelos grandes aunque siga trabajando. Ya no se
        // abre una consola aparte para mostrar un heartbeat — tapaba la barra
        // de progreso nativa de Revit (abajo a la izquierda) sin aportar nada
        // útil a cambio.
        try
        {
            using var tx = new Transaction(doc, "Exportar IFC");
            tx.Start();
            doc.Export(folderPath, fileName, ifcOptions);
            tx.Commit();
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Exportar IFC – Error", ex.Message);
            return Result.Failed;
        }

        string rutaCompleta = Path.Combine(folderPath, fileName);

        TaskDialog.Show(
            "Exportar IFC",
            $"✔ Exportación completada.\nVista: {vista3D.Name}\n\n{rutaCompleta}");

        return Result.Succeeded;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers privados
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Devuelve la vista activa si es 3D; si no, muestra un picker con las
    /// vistas 3D disponibles. Retorna null si el usuario cancela.
    /// </summary>
    private static View3D? ResolveView3D(Document doc, UIApplication uiApp)
    {
        // Vista activa 3D → usarla directamente
        if (uiApp.ActiveUIDocument.ActiveView is View3D activeView3D)
            return activeView3D;

        // Recopilar todas las vistas 3D no-template del documento
        var vistas3D = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .Where(v => !v.IsTemplate)
            .OrderBy(v => v.Name)
            .ToList();

        if (vistas3D.Count == 0)
        {
            TaskDialog.Show("Exportar IFC",
                "No hay vistas 3D en el documento.\n\nCrea una vista 3D primero.");
            return null;
        }

        // Mostrar picker
        var picker = new View3DPickerWindow(vistas3D);
        if (picker.ShowDialog() != true) return null;

        return picker.SelectedView;
    }

    private static string PromptFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Selecciona la carpeta de destino para el archivo IFC",
        };
        return dlg.ShowDialog() == true ? dlg.FolderName : string.Empty;
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Ventana de selección de vista 3D (solo se muestra si la activa no es 3D)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Ventana WPF mínima para elegir una vista 3D de la lista del documento.
/// </summary>
internal class View3DPickerWindow : Window
{
    private readonly ListBox _listBox;
    private readonly List<View3D> _vistas;

    public View3D? SelectedView { get; private set; }

    public View3DPickerWindow(List<View3D> vistas)
    {
        _vistas = vistas;

        Title = "Exportar IFC – Elegir vista 3D";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 520;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var stack = new StackPanel { Margin = new Thickness(16) };

        stack.Children.Add(new TextBlock
        {
            Text = "La vista activa no es una vista 3D.\nSelecciona la vista 3D a exportar:",
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });

        _listBox = new ListBox
        {
            Height = 240,
            Margin = new Thickness(0, 0, 0, 14),
        };
        foreach (var v in vistas)
            _listBox.Items.Add(v.Name);
        if (_listBox.Items.Count > 0)
            _listBox.SelectedIndex = 0;

        _listBox.MouseDoubleClick += (_, _) => Confirmar();

        stack.Children.Add(_listBox);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var btnOk = new Button
        {
            Content = "Exportar",
            Width = 90,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        btnOk.Click += (_, _) => Confirmar();

        var btnCancel = new Button
        {
            Content = "Cancelar",
            Width = 90,
            Height = 28,
            IsCancel = true,
        };
        btnCancel.Click += (_, _) => { DialogResult = false; };

        btns.Children.Add(btnOk);
        btns.Children.Add(btnCancel);
        stack.Children.Add(btns);

        Content = stack;
    }

    private void Confirmar()
    {
        int idx = _listBox.SelectedIndex;
        if (idx < 0)
        {
            System.Windows.MessageBox.Show("Selecciona una vista.", "Exportar IFC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedView = _vistas[idx];
        DialogResult = true;
    }
}
