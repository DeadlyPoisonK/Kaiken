using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;
using RevitColor = Autodesk.Revit.DB.Color;
using WpfColor = System.Windows.Media.Color;
using WpfVisibility = System.Windows.Visibility;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Kaiken;

/// <summary>
/// Panel acoplable de Capas (ver LayerStore). Todo lo que toca el modelo va por un ExternalEvent;
/// la lista se rehace al cambiar de vista, al deshacer y tras cada acción. Se crea de dos formas:
/// en OnStartup (panel definitivo, con UIControlledApplication) o desde el modo desarrollo (con
/// UIApplication); en ambos casos se suscribe a los eventos de Revit y Dispose los suelta.
/// Los colores siguen el tema de Revit (claro/oscuro) y los de Opciones > Colores.
/// </summary>
public partial class LayersPanel : UserControl, IDisposable
{
    private const string Title = "Capas";
    private const string TxPrefix = "Kaiken: Capas - ";

    private readonly ObservableCollection<LayerRow> _rows = new();
    private readonly Handler _handler = new();
    private readonly ExternalEvent _event;
    private readonly List<Action> _unsubscribe = new();
    /// <summary>Durante una acción propia no se reacciona a DocumentChanged (la acción ya refresca).</summary>
    private bool _busy;
    private string? _docKey;

    /// <summary>Modo desarrollo: corre en contexto de la API, así que ya muestra la vista activa.</summary>
    public LayersPanel(UIApplication app) : this()
    {
        app.ViewActivated += OnViewActivated;
        app.ThemeChanged += OnThemeChanged;
        app.Application.DocumentChanged += OnDocumentChanged;
        app.Application.DocumentClosed += OnDocumentClosed;
        _unsubscribe.Add(() =>
        {
            app.ViewActivated -= OnViewActivated;
            app.ThemeChanged -= OnThemeChanged;
            app.Application.DocumentChanged -= OnDocumentChanged;
            app.Application.DocumentClosed -= OnDocumentClosed;
        });
        Refresh(app, counts: true);
    }

    /// <summary>Panel definitivo, creado en OnStartup (todavía no hay documento).</summary>
    internal LayersPanel(UIControlledApplication app) : this()
    {
        app.ViewActivated += OnViewActivated;
        app.ThemeChanged += OnThemeChanged;
        app.ControlledApplication.DocumentChanged += OnDocumentChanged;
        app.ControlledApplication.DocumentClosed += OnDocumentClosed;
        _unsubscribe.Add(() =>
        {
            app.ViewActivated -= OnViewActivated;
            app.ThemeChanged -= OnThemeChanged;
            app.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            app.ControlledApplication.DocumentClosed -= OnDocumentClosed;
        });
        ShowNoDocument();
    }

    private LayersPanel()
    {
        InitializeComponent();
        ApplyTheme();
        _event = ExternalEvent.Create(_handler);
        LayerList.ItemsSource = _rows;
    }

    public void Dispose()
    {
        foreach (var u in _unsubscribe)
            try { u(); } catch { /* Revit cerrando */ }
        _unsubscribe.Clear();
        _event.Dispose();
    }

    // ── Tema ───────────────────────────────────────────────────────────────────

    /// <summary>Neutros de la interfaz de Revit 2025 (fondo de Propiedades / Project Browser y derivados).</summary>
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["K.Bg"] = "#3B4453", ["K.Header"] = "#323A47", ["K.Input"] = "#2C323E", ["K.Menu"] = "#2C323E",
        ["K.Divider"] = "#333A46", ["K.Border"] = "#525C6C", ["K.Hover"] = "#465062", ["K.Pressed"] = "#2C323E",
        ["K.Fg"] = "#F0F2F5", ["K.Muted"] = "#A3ACB9", ["K.Scroll"] = "#5A6577",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["K.Bg"] = "#FAFAFA", ["K.Header"] = "#EEEEEE", ["K.Input"] = "#FFFFFF", ["K.Menu"] = "#FFFFFF",
        ["K.Divider"] = "#E4E4E4", ["K.Border"] = "#C8C8C8", ["K.Hover"] = "#E6EDF5", ["K.Pressed"] = "#D3DDE8",
        ["K.Fg"] = "#1F1F1F", ["K.Muted"] = "#6B6B6B", ["K.Scroll"] = "#C4C4C4",
    };

    private static WpfColor Wpf(RevitColor c) => WpfColor.FromRgb(c.Red, c.Green, c.Blue);

    /// <summary>Pone los pinceles K.* según el tema de la interfaz y los colores de Opciones. Requiere contexto de la API.</summary>
    private void ApplyTheme()
    {
        bool dark = true;
        try { dark = UIThemeManager.CurrentTheme == UITheme.Dark; } catch { /* sin tema: oscuro */ }

        // Acento = preselección y avisos = alerta, de Opciones > Colores (por defecto, los de Revit).
        WpfColor accent = WpfColor.FromRgb(6, 165, 225), alert = WpfColor.FromRgb(255, 68, 68);
        try
        {
            var options = ColorOptions.GetColorOptions();
            accent = Wpf(options.PreselectionColor);
            alert = Wpf(options.AlertColor);
        }
        catch { /* versión sin ColorOptions */ }

        foreach (var (key, hex) in dark ? Dark : Light)
            Resources[key] = Frozen((WpfColor)ColorConverter.ConvertFromString(hex));
        Resources["K.Accent"] = Frozen(accent);
        Resources["K.Selected"] = Frozen(WpfColor.FromArgb(dark ? (byte)0x50 : (byte)0x30, accent.R, accent.G, accent.B));
        Resources["K.Alert"] = Frozen(alert);
        Resources["K.AlertBg"] = Frozen(WpfColor.FromArgb(0x26, alert.R, alert.G, alert.B));
    }

    private static SolidColorBrush Frozen(WpfColor c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e) => ApplyTheme();

    // ── Eventos de Revit ───────────────────────────────────────────────────────

    private void OnViewActivated(object? sender, ViewActivatedEventArgs e)
    {
        if (sender is UIApplication app) SafeRefresh(app, counts: true);
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_busy || sender is not RevitApplication application) return;
        var app = new UIApplication(application);
        Document? active = app.ActiveUIDocument?.Document;
        if (active == null || !e.GetDocument().Equals(active)) return;

        // Deshacer/rehacer o una transacción de capas: todo, conteos incluidos.
        bool undo = e.Operation is UndoOperation.TransactionUndone or UndoOperation.TransactionRedone;
        bool ours = e.GetTransactionNames().Any(n => n.StartsWith(TxPrefix, StringComparison.Ordinal));
        if (undo || ours) { SafeRefresh(app, counts: true); return; }

        // Si no, solo si cambió un filtro o la vista activa (ej. la editaron en Visibilidad/Gráficos).
        ElementId? viewId = app.ActiveUIDocument?.ActiveView?.Id;
        bool relevant = e.GetDeletedElementIds().Count > 0
            || e.GetAddedElementIds().Concat(e.GetModifiedElementIds())
                .Any(id => id == viewId || active.GetElement(id) is ParameterFilterElement);
        if (relevant) SafeRefresh(app, counts: false);
    }

    // Al cerrar no hay vista nueva activa todavía: se refresca cuando Revit quede libre.
    private void OnDocumentClosed(object? sender, DocumentClosedEventArgs e) => Run(app => Refresh(app, counts: true));

    private void SafeRefresh(UIApplication app, bool counts)
    {
        try { Refresh(app, counts); }
        catch (Exception ex) { App.BridgeLog($"capas: error al refrescar: {ex}"); }
    }

    // ── Lista ──────────────────────────────────────────────────────────────────

    private void ShowNoDocument()
    {
        _docKey = null;
        _rows.Clear();
        ViewText.Text = "Sin modelo abierto";
        SetWarning(null);
        EmptyText.Text = "Abre un modelo para ver sus capas.";
        EmptyPanel.Visibility = WpfVisibility.Visible;
        IsEnabled = false;
    }

    /// <summary>Rehace la lista con el modelo y la vista activa. Requiere contexto de la API.</summary>
    private void Refresh(UIApplication app, bool counts)
    {
        UIDocument? uidoc = app.ActiveUIDocument;
        if (uidoc == null || uidoc.Document.IsFamilyDocument) { ShowNoDocument(); return; }
        IsEnabled = true;
        Document doc = uidoc.Document;
        View? view = uidoc.ActiveView;
        string key = doc.PathName + "|" + doc.Title;
        if (key != _docKey) counts = true;
        _docKey = key;
        if (counts) ApplyTheme(); // por si cambiaron los colores en Opciones

        var state = LayerStore.ViewState(doc, view);
        var layers = LayerStore.Load(doc, state.Unsupported ? null : view, counts);
        var oldCounts = _rows.ToDictionary(r => r.Info.FilterId, r => r.Info.Count);
        if (!counts)
            foreach (var l in layers)
                if (oldCounts.TryGetValue(l.FilterId, out int n)) l.Count = n;

        ElementId? selected = Current?.Info.FilterId;
        _rows.Clear();
        foreach (var l in layers) _rows.Add(new LayerRow(l, !state.Unsupported));
        LayerList.SelectedItem = _rows.FirstOrDefault(r => r.Info.FilterId == selected);

        ViewText.Text = view?.Name ?? doc.Title;
        ViewText.ToolTip = $"{doc.Title} · {view?.Name}";
        SetWarning(state.Unsupported
            ? "Esta vista no admite capas (planillas, láminas o vistas sin overrides): el ojo, el color y el semitono no hacen nada aquí."
            : state.FilterBlock);
        EmptyText.Text = "Sin capas. Selecciona elementos en Revit y pulsa + abajo para crear la primera.";
        EmptyPanel.Visibility = _rows.Count == 0 ? WpfVisibility.Visible : WpfVisibility.Collapsed;
    }

    private void SetWarning(string? text)
    {
        WarningText.Text = text ?? "";
        WarningBox.Visibility = text == null ? WpfVisibility.Collapsed : WpfVisibility.Visible;
    }

    private void SetStatus(string? text)
    {
        StatusText.Text = text ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(text) ? WpfVisibility.Collapsed : WpfVisibility.Visible;
    }

    private LayerRow? Current => LayerList.SelectedItem as LayerRow;

    private static LayerRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as LayerRow;

    // ── Acciones ───────────────────────────────────────────────────────────────

    private void Run(Action<UIApplication> action)
    {
        _handler.Enqueue(action);
        _event.Raise();
    }

    /// <summary>Corre <paramref name="body"/> en una transacción y refresca. body devuelve el mensaje para la barra de estado.</summary>
    private void Transact(string name, Func<UIDocument, string?> body) => Run(app =>
    {
        UIDocument? uidoc = app.ActiveUIDocument;
        if (uidoc == null) return;
        string? status;
        _busy = true;
        try
        {
            using var tx = new Transaction(uidoc.Document, TxPrefix + name);
            tx.Start();
            status = body(uidoc);
            tx.Commit();
        }
        catch (Exception ex)
        {
            status = null;
            App.BridgeLog($"capas: error en {name}: {ex}");
            TaskDialog.Show(Title, ex.Message);
        }
        finally { _busy = false; }
        SafeRefresh(app, counts: true);
        if (status != null) SetStatus(status);
    });

    private static string Report(string what, LayerAssignResult r)
    {
        if (r.Skipped.Count > 0)
        {
            new TaskDialog(Title)
            {
                MainInstruction = $"{r.Skipped.Count} elemento(s) no se pudieron cambiar",
                MainContent = string.Join("\n", r.Skipped.Take(15)) + (r.Skipped.Count > 15 ? $"\n… y {r.Skipped.Count - 15} más" : ""),
            }.Show();
        }
        return $"{what}: {r.Done} elemento(s)" + (r.Skipped.Count > 0 ? $", {r.Skipped.Count} omitido(s)" : "") + ".";
    }

    /// <summary>Como en Photoshop: crea «Capa N» (con la selección, si hay) y deja el nombre listo para escribir.</summary>
    private void Create_Click(object sender, RoutedEventArgs e) => Transact("Nueva capa", uidoc =>
    {
        Document doc = uidoc.Document;
        string name = LayerStore.DefaultLayerName(doc);
        var filter = LayerStore.Create(doc, name);
        var selection = uidoc.Selection.GetElementIds();
        string msg = $"Capa «{name}» creada";
        if (selection.Count > 0) msg = Report(msg + " con la selección", LayerStore.Assign(doc, filter.Id, selection, uidoc.ActiveView));

        // Después del refresco: marcarla y abrir el nombre para editarlo.
        Dispatcher.InvokeAsync(() =>
        {
            if (_rows.FirstOrDefault(r => r.Info.FilterId == filter.Id) is not { } row) return;
            LayerList.SelectedItem = row;
            LayerList.ScrollIntoView(row);
            row.BeginEdit();
        });
        return msg;
    });

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } row) { SetStatus("Marca primero una capa en la lista."); return; }
        Transact("Asignar", uidoc =>
        {
            var selection = uidoc.Selection.GetElementIds();
            if (selection.Count == 0) return "No hay nada seleccionado en Revit.";
            return Report($"Asignados a «{row.Info.Name}»", LayerStore.Assign(uidoc.Document, row.Info.FilterId, selection, uidoc.ActiveView));
        });
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => Transact("Quitar", uidoc =>
    {
        var selection = uidoc.Selection.GetElementIds();
        if (selection.Count == 0) return "No hay nada seleccionado en Revit.";
        return Report("Quitados de su capa", LayerStore.Remove(uidoc.Document, selection));
    });

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } row) SelectMembers(row, inView: true);
        else SetStatus("Marca primero una capa en la lista.");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } row) Delete(row);
        else SetStatus("Marca primero una capa en la lista.");
    }

    private void LayerList_KeyDown(object sender, KeyEventArgs e)
    {
        if (Current is not { IsEditing: false } row) return;
        if (e.Key == Key.F2) { row.BeginEdit(); e.Handled = true; }
        else if (e.Key == Key.Delete) { Delete(row); e.Handled = true; }
    }

    private void SelectMembers(LayerRow row, bool inView) => Run(app =>
    {
        UIDocument? uidoc = app.ActiveUIDocument;
        if (uidoc == null) return;
        try
        {
            var ids = LayerStore.MemberIds(uidoc.Document, row.Info.FilterId, inView ? uidoc.ActiveView : null);
            uidoc.Selection.SetElementIds(ids);
            SetStatus($"«{row.Info.Name}»: {ids.Count} elemento(s) seleccionado(s){(inView ? " en la vista" : " en el modelo")}.");
        }
        catch (Exception ex) { TaskDialog.Show(Title, ex.Message); }
    });

    private void Refresh_Click(object sender, RoutedEventArgs e) => Run(app => { Refresh(app, counts: true); SetStatus(null); });

    private void ShowAll_Click(object sender, RoutedEventArgs e) =>
        ApplyState("Mostrar todas", _rows.ToList(), (l, _) => l.Visible = true);

    /// <summary>Cambia el estado de una o más capas en la vista activa.</summary>
    private void ApplyState(string name, List<LayerRow> rows, Action<LayerInfo, LayerRow> change) => Transact(name, uidoc =>
    {
        View view = uidoc.ActiveView;
        string? warning = null;
        foreach (var row in rows)
        {
            change(row.Info, row);
            warning ??= LayerStore.Apply(uidoc.Document, view, row.Info);
        }
        return warning != null ? "La plantilla controla los filtros: solo cambiaron las anotaciones." : null;
    });

    private void Eye_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Como Alt+clic en Photoshop: solo esta capa (o todas, si ya estaba sola).
            bool alone = row.Info.Visible && _rows.Where(r => r != row).All(r => !r.Info.Visible);
            ApplyState("Solo esta capa", _rows.ToList(), (l, r) => l.Visible = alone || r == row);
            return;
        }
        ApplyState("Ojo", new() { row }, (l, _) => l.Visible = !l.Visible);
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ApplyState("Color", new() { row }, (l, _) => l.ColorOn = !l.ColorOn);
    }

    private void Halftone_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ApplyState("Semitono", new() { row }, (l, _) => l.Halftone = !l.Halftone);
    }

    private void SetColor(LayerRow row, RevitColor color) => Transact("Color de capa", uidoc =>
    {
        LayerStore.SetColor(uidoc.Document, row.Info.FilterId, color);
        row.Info.Color = color;
        row.Info.ColorOn = true;
        return LayerStore.Apply(uidoc.Document, uidoc.ActiveView, row.Info) != null
            ? "La plantilla controla los filtros: el color solo se ve en las anotaciones." : null;
    });

    private void Delete(LayerRow row)
    {
        var confirm = new TaskDialog(Title)
        {
            MainInstruction = $"¿Borrar la capa «{row.Info.Name}»?",
            MainContent = "Sus elementos quedan sin capa (no se borran) y sus anotaciones vuelven a verse. Se puede deshacer con Ctrl+Z.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        if (confirm.Show() != TaskDialogResult.Yes) return;
        Transact("Borrar capa", uidoc => Report($"Capa «{row.Info.Name}» borrada; sin capa", LayerStore.Delete(uidoc.Document, row.Info.FilterId)));
    }

    // ── Renombrar en la fila (doble clic en el nombre o F2) ────────────────────

    private void Name_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && RowOf(sender) is { } row) row.BeginEdit();
    }

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
            box.Dispatcher.InvokeAsync(() => { box.Focus(); box.SelectAll(); });
    }

    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (e.Key == Key.Enter) { CommitRename(row); e.Handled = true; }
        else if (e.Key == Key.Escape) { row.EndEdit(); e.Handled = true; }
    }

    private void EditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { IsEditing: true } row) CommitRename(row);
    }

    private void CommitRename(LayerRow row)
    {
        string name = row.EditName.Trim();
        row.EndEdit();
        if (name.Length == 0 || name == row.Info.Name) return;
        Transact("Renombrar", uidoc =>
        {
            LayerStore.Rename(uidoc.Document, row.Info.FilterId, name);
            return $"Capa renombrada a «{name}».";
        });
    }

    // ── Menú contextual de la fila ─────────────────────────────────────────────

    private void Row_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement fe || RowOf(sender) is not { } row || fe.ContextMenu is not { } menu) return;
        LayerList.SelectedItem = row;
        menu.Items.Clear();

        MenuItem Item(string glyph, string header, Action click)
        {
            var mi = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph, FontSize = 13 } };
            mi.Click += (_, _) => click();
            return mi;
        }

        menu.Items.Add(Item("", "Seleccionar en la vista", () => SelectMembers(row, inView: true)));
        menu.Items.Add(Item("", "Seleccionar en todo el modelo", () => SelectMembers(row, inView: false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("", "Solo esta capa", () => ApplyState("Solo esta capa", _rows.ToList(), (l, r) => l.Visible = r == row)));
        menu.Items.Add(Item("", "Mostrar todas", () => ApplyState("Mostrar todas", _rows.ToList(), (l, _) => l.Visible = true)));
        menu.Items.Add(new Separator());

        // Paleta en el mismo menú (sin submenú): un clic cambia el color y lo prende.
        var palette = new WrapPanel { Width = 6 * 24, Margin = new Thickness(28, 2, 6, 4) };
        foreach (var c in LayerStore.Palette)
        {
            var color = c;
            bool current = c.Red == row.Info.Color.Red && c.Green == row.Info.Color.Green && c.Blue == row.Info.Color.Blue;
            var swatch = new Button
            {
                Style = (Style)FindResource("RowButton"),
                Content = new Ellipse
                {
                    Width = 14, Height = 14,
                    Fill = new SolidColorBrush(WpfColor.FromRgb(c.Red, c.Green, c.Blue)),
                    Stroke = current ? (Brush)FindResource("K.Fg") : null,
                    StrokeThickness = 2,
                },
            };
            swatch.Click += (_, _) => { menu.IsOpen = false; SetColor(row, color); };
            palette.Children.Add(swatch);
        }
        menu.Items.Add(new TextBlock { Text = "Color", Margin = new Thickness(34, 4, 6, 0), Foreground = (Brush)FindResource("K.Muted"), FontSize = 11 });
        menu.Items.Add(palette);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("", "Renombrar   F2", row.BeginEdit));
        menu.Items.Add(Item("", "Borrar capa…   Supr", () => Delete(row)));
    }

    // ── Tipos auxiliares ───────────────────────────────────────────────────────

    private sealed class Handler : IExternalEventHandler
    {
        private readonly Queue<Action<UIApplication>> _queue = new();

        public void Enqueue(Action<UIApplication> a)
        {
            lock (_queue) _queue.Enqueue(a);
        }

        public void Execute(UIApplication app)
        {
            while (true)
            {
                Action<UIApplication> a;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    a = _queue.Dequeue();
                }
                try { a(app); }
                catch (Exception ex)
                {
                    App.BridgeLog($"capas: {ex}");
                    TaskDialog.Show(Title, ex.Message);
                }
            }
        }

        public string GetName() => "Kaiken Capas";
    }
}

/// <summary>Fila de la lista de capas (pública: el binding de WPF no ve tipos internos).</summary>
public sealed class LayerRow : INotifyPropertyChanged
{
    internal LayerInfo Info { get; }
    private readonly bool _viewSupported;
    private bool _editing;
    private string _editName = "";

    internal LayerRow(LayerInfo info, bool viewSupported)
    {
        Info = info;
        _viewSupported = viewSupported;
    }

    public string Name => Info.Name;
    public string CountText => Info.Count.ToString();
    public string EyeGlyph => Info.Visible ? "" : "";
    public double EyeOpacity => _viewSupported ? 1.0 : 0.4;
    public Brush Swatch => new SolidColorBrush(WpfColor.FromRgb(Info.Color.Red, Info.Color.Green, Info.Color.Blue));
    public Brush SwatchFill => Info.ColorOn ? Swatch : Brushes.Transparent;
    public double HalftoneOpacity => Info.Halftone ? 1.0 : 0.3;
    /// <summary>Capa apagada: la fila entera queda tenue, como en Photoshop.</summary>
    public double RowOpacity => Info.Visible ? 1.0 : 0.55;

    public bool IsEditing => _editing;
    public WpfVisibility NameVisibility => _editing ? WpfVisibility.Collapsed : WpfVisibility.Visible;
    public WpfVisibility EditVisibility => _editing ? WpfVisibility.Visible : WpfVisibility.Collapsed;

    public string EditName
    {
        get => _editName;
        set { _editName = value; OnChanged(); }
    }

    public void BeginEdit()
    {
        EditName = Info.Name;
        _editing = true;
        OnChanged(nameof(NameVisibility));
        OnChanged(nameof(EditVisibility));
    }

    public void EndEdit()
    {
        _editing = false;
        OnChanged(nameof(NameVisibility));
        OnChanged(nameof(EditVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
