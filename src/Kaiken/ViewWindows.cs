using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.UI;
using ElementId = Autodesk.Revit.DB.ElementId;
using RvtView = Autodesk.Revit.DB.View;
using ViewSheet = Autodesk.Revit.DB.ViewSheet;

namespace Kaiken;

/// <summary>
/// Posición de las ventanas de vista, que la Revit API no expone. En Revit 2025/2026 cada vista es
/// una pestaña (LayoutDocument) de AvalonDock (Xceed 2.1) dentro de la ventana principal, y cada
/// vista arrastrada fuera queda sola en su propia ventana flotante, que puede estar en otro monitor.
/// Se accede por reflexión porque esas DLL solo existen en la instalación de Revit (el build de CI
/// no las tiene); si en otra versión cambian, <see cref="Find"/> devuelve null y Guardar/Restaurar
/// Vistas siguen funcionando, solo que sin posiciones.
/// Las posiciones se guardan como WINDOWPLACEMENT de Win32 (estado + rectángulo en píxeles), así
/// que sirven para cualquier monitor y recuerdan si la ventana estaba maximizada.
/// </summary>
internal sealed class ViewWindows
{
    private const string DockingManagerType = "Xceed.Wpf.AvalonDock.DockingManager";
    private const string LayoutDocumentType = "Xceed.Wpf.AvalonDock.Layout.LayoutDocument";
    private const string FloatingWindowType = "Xceed.Wpf.AvalonDock.Layout.LayoutFloatingWindow";

    private readonly object _manager;
    private readonly Matrix _fromDevice; // píxeles -> unidades WPF

    private ViewWindows(object manager, Matrix fromDevice)
    {
        _manager = manager;
        _fromDevice = fromDevice;
    }

    public static ViewWindows? Find(IntPtr mainHwnd)
    {
        if (HwndSource.FromHwnd(mainHwnd) is not HwndSource src || src.RootVisual == null) return null;
        Matrix fromDevice = src.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        // El DockingManager de las vistas está en el árbol visual de la ventana principal.
        var stack = new Stack<DependencyObject>();
        stack.Push(src.RootVisual);
        while (stack.Count > 0)
        {
            DependencyObject cur = stack.Pop();
            if (IsType(cur, DockingManagerType))
            {
                var vw = new ViewWindows(cur, fromDevice);
                if (vw.Tabs().Any()) return vw;
                continue;
            }
            int n = VisualTreeHelper.GetChildrenCount(cur);
            for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(cur, i));
        }
        return null;
    }

    /// <summary>
    /// Asocia cada vista abierta del documento con su pestaña, por el título de la ventana de Revit
    /// ("&lt;tipo&gt;: &lt;vista&gt; - &lt;archivo&gt;"). Las vistas que no se puedan identificar quedan fuera.
    /// </summary>
    public Dictionary<ElementId, object> MapOpenViews(UIDocument uidoc)
    {
        var frames = Tabs()
            .Select(t => (Tab: t, Title: WindowText(FrameHwnd(t)), Active: Get(Get(t, "Content"), "isActive") is true))
            .ToList();
        RvtView activeView = uidoc.ActiveView;

        // El sufijo del archivo sale de la ventana activa, que es la vista activa de este documento;
        // así no se confunden vistas homónimas de otros modelos abiertos.
        var active = frames.FirstOrDefault(f => f.Active);
        string? suffix = null;
        if (active.Tab != null)
        {
            foreach (string label in Labels(activeView))
            {
                int i = active.Title.IndexOf(": " + label + " - ", StringComparison.Ordinal);
                if (i < 0) continue;
                suffix = active.Title[(i + label.Length + 5)..];
                break;
            }
        }

        var map = new Dictionary<ElementId, object>();
        var used = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (suffix != null)
        {
            map[activeView.Id] = active.Tab!;
            used.Add(active.Tab!);
        }

        foreach (UIView uv in uidoc.GetOpenUIViews())
        {
            if (map.ContainsKey(uv.ViewId) || uidoc.Document.GetElement(uv.ViewId) is not RvtView v) continue;
            string[] labels = Labels(v);
            var match = frames.FirstOrDefault(f => !used.Contains(f.Tab) && labels.Any(l => suffix != null
                ? f.Title.EndsWith(": " + l + " - " + suffix, StringComparison.Ordinal)
                : f.Title.Contains(": " + l + " - ", StringComparison.Ordinal)));
            if (match.Tab == null) continue;
            map[v.Id] = match.Tab;
            used.Add(match.Tab);
        }
        return map;
    }

    /// <summary>Posición de la ventana flotante de la pestaña, o null si está acoplada en la ventana principal.</summary>
    public int[]? FloatingPlacement(object tab) =>
        Get(tab, "IsFloating") is true && FloatingWindow(tab) is Window w ? GetPlacement(new WindowInteropHelper(w).Handle) : null;

    /// <summary>Saca la pestaña a una ventana flotante (si no lo está ya) y la lleva a la posición guardada.</summary>
    public bool Float(object tab, int[] placement)
    {
        if (placement.Length != 5) return false;
        if (Get(tab, "IsFloating") is not true)
        {
            // Posición aproximada para la ventana nueva, por si después no se la encuentra.
            Point tl = _fromDevice.Transform(new Point(placement[1], placement[2]));
            Point br = _fromDevice.Transform(new Point(placement[3], placement[4]));
            Set(tab, "FloatingLeft", tl.X);
            Set(tab, "FloatingTop", tl.Y);
            Set(tab, "FloatingWidth", br.X - tl.X);
            Set(tab, "FloatingHeight", br.Y - tl.Y);
            Call(tab, "Float");
        }
        if (FloatingWindow(tab) is not Window w) return false;
        bool ok = SetPlacement(new WindowInteropHelper(w).Handle, placement);
        w.UpdateLayout(); // que la vista tome el tamaño nuevo antes de aplicarle el zoom
        return ok;
    }

    /// <summary>Devuelve a la ventana principal una pestaña que esté flotando.</summary>
    public void Dock(object tab)
    {
        if (Get(tab, "IsFloating") is true) Call(tab, "DockAsDocument");
    }

    private IEnumerable<object> Tabs()
    {
        object? layout = Get(_manager, "Layout");
        var roots = new List<object?> { Get(layout, "RootPanel") };
        if (Get(layout, "FloatingWindows") is IEnumerable fws) roots.AddRange(fws.Cast<object>());

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (object? root in roots)
        {
            if (root == null) continue;
            var stack = new Stack<object>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                object cur = stack.Pop();
                if (IsType(cur, LayoutDocumentType))
                {
                    if (FrameHwnd(cur) != IntPtr.Zero && seen.Add(cur)) yield return cur;
                    continue;
                }
                if (Get(cur, "Children") is IEnumerable children)
                    foreach (object? c in children)
                        if (c != null) stack.Push(c);
            }
        }
    }

    /// <summary>Ventana de Revit (MFC) que muestra la vista; su contenido es un UIFramework.MFCMDIChildFrameControl.</summary>
    private static IntPtr FrameHwnd(object tab) => Get(Get(tab, "Content"), "hostHWnd") is IntPtr h ? h : IntPtr.Zero;

    private Window? FloatingWindow(object tab)
    {
        object? fw = Get(tab, "Parent");
        while (fw != null && !IsType(fw, FloatingWindowType)) fw = Get(fw, "Parent");
        if (fw == null || Get(_manager, "FloatingWindows") is not IEnumerable controls) return null;
        foreach (object c in controls)
            if (c is Window w && ReferenceEquals(Get(c, "Model"), fw)) return w;
        return null;
    }

    private static string[] Labels(RvtView v) =>
        v is ViewSheet s ? new[] { $"{s.SheetNumber} - {s.Name}", s.Name } : new[] { v.Name };

    // ── Reflexión ────────────────────────────────────────────────────────────

    private static bool IsType(object o, string fullName)
    {
        for (Type? t = o.GetType(); t != null; t = t.BaseType)
            if (t.FullName == fullName) return true;
        return false;
    }

    private static PropertyInfo? Prop(object o, string name) =>
        o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0);

    private static object? Get(object? o, string name)
    {
        if (o == null) return null;
        try { return Prop(o, name)?.GetValue(o); }
        catch { return null; }
    }

    private static void Set(object o, string name, object value)
    {
        try { Prop(o, name)?.SetValue(o, value); }
        catch { /* solo es la posición aproximada */ }
    }

    private static void Call(object o, string name) =>
        o.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)?.Invoke(o, null);

    // ── Win32 ────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length, Flags, ShowCmd;
        public int MinX, MinY, MaxX, MaxY;
        public RECT Normal;
    }

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMAXIMIZED = 3;

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rc, uint flags); // flags 0 = null si no toca ningún monitor

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

    private static string WindowText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>[estado, izquierda, arriba, derecha, abajo] de la ventana sin maximizar.</summary>
    public static int[]? GetPlacement(IntPtr hwnd)
    {
        var wp = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (hwnd == IntPtr.Zero || !GetWindowPlacement(hwnd, ref wp)) return null;
        return new[] { wp.ShowCmd, wp.Normal.Left, wp.Normal.Top, wp.Normal.Right, wp.Normal.Bottom };
    }

    /// <summary>Aplica una posición guardada. No hace nada si cae en un monitor que ya no está conectado.</summary>
    public static bool SetPlacement(IntPtr hwnd, int[] p)
    {
        if (hwnd == IntPtr.Zero || p.Length != 5 || p[3] <= p[1] || p[4] <= p[2]) return false;
        var rc = new RECT { Left = p[1], Top = p[2], Right = p[3], Bottom = p[4] };
        if (MonitorFromRect(ref rc, 0) == IntPtr.Zero) return false;
        var wp = new WINDOWPLACEMENT
        {
            Length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            ShowCmd = p[0] == SW_SHOWMAXIMIZED ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL, // minimizada -> normal
            MinX = -1, MinY = -1, MaxX = -1, MaxY = -1,
            Normal = rc,
        };
        return SetWindowPlacement(hwnd, ref wp);
    }
}
