using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Modo desarrollo: carga builds de prueba de Kaiken sin reiniciar Revit. Apagado por defecto; se
/// activa con "devMode": { "enabled": true } en %APPDATA%\Kaiken\settings.json y un reinicio (el
/// botón Dev y el panel acoplable solo se pueden registrar al arrancar).
///
/// tools\dev-build.ps1 compila este mismo proyecto, más la carpeta Dev\, con un nombre de assembly
/// único (KaikenDev_yyyyMMdd_HHmmss.dll) en %APPDATA%\Kaiken\dev\&lt;versión de Revit&gt;\. El nombre
/// único es lo que permite recargar: .NET no descarga un assembly ya cargado, y WPF resuelve los
/// pack URI (XAML e íconos) por nombre de assembly, así que con el mismo nombre seguiría usando la
/// versión anterior.
///
/// Lo que hay que tener en cuenta en el código que se prueba así:
/// - La DLL dev se carga en el mismo AssemblyLoadContext que Kaiken (Revit 2026 le da uno propio a
///   cada add-in), para que los tipos de RevitAPI y WPF sean los mismos.
/// - Tiene sus propios estáticos: App.Instance es null (App.OnStartup no corre en ella), los cachés
///   y ventanas no modales empiezan vacíos, y la cinta, el puente en vivo, los eventos registrados
///   al arrancar y el panel acoplable siguen siendo los de Kaiken instalado.
/// - Cada recarga deja la versión anterior en memoria hasta cerrar Revit, con lo que haya
///   registrado. Un IUpdater dev debe desregistrar el anterior (mismo UpdaterId) antes de
///   registrarse, o quedan los dos activos; lo mismo con los eventos de Revit. El contenido del
///   panel que implemente IDisposable recibe Dispose al ser reemplazado: ahí se limpia.
/// - Los botones nuevos de la cinta siguen necesitando reinicio: se prueban desde el botón Dev.
/// </summary>
internal static class DevMode
{
    private const string Title = "Kaiken Dev";

    /// <summary>Namespace del código de prueba (carpeta Dev\): sale primero en las listas.</summary>
    private const string DevNamespace = "Kaiken.Dev";

    internal static readonly DockablePaneId PaneId = new(new Guid("6b1f3c52-8f0e-4a7d-9c1b-2e5d7a4f9e31"));

    /// <summary>DLL dev ya cargadas, por ruta: cargar dos veces la misma no tiene sentido (no se descarga).</summary>
    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private static DevPane? _pane;

    /// <summary>Último tipo elegido en el botón Dev, para dejarlo marcado al volver a abrirlo.</summary>
    internal static string? LastChoice;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // NaN/Infinity no rompen la respuesta
    };

    /// <summary>Panel "Dev" de la cinta y panel acoplable contenedor. Nunca debe tumbar la carga del add-in.</summary>
    internal static void Setup(UIControlledApplication application, string tabName, string assemblyPath)
    {
        try
        {
            RibbonPanel devPanel = application.CreateRibbonPanel(tabName, "Dev");
            devPanel.AddItem(new PushButtonData("KaikenDevCommand", "Dev", assemblyPath, "Kaiken.DevCommand")
            {
                LargeImage = App.LoadIcon("dev_32.png"),
                ToolTip = "Modo desarrollo: carga el build de prueba más reciente (tools\\dev-build.ps1) sin reiniciar Revit " +
                          "y ejecuta uno de sus comandos, o muestra uno de sus paneles en el panel acoplable \"Kaiken Dev\".",
            });

            _pane = new DevPane();
            application.RegisterDockablePane(PaneId, Title, _pane);
            App.BridgeLog("dev: modo desarrollo activo (botón Dev y panel Kaiken Dev registrados)");
        }
        catch (Exception ex)
        {
            App.BridgeLog($"dev: no se pudo preparar el modo desarrollo: {ex}");
        }
    }

    internal static string Folder(string revitVersion) => Path.Combine(KaikenSettings.FolderPath, "dev", revitVersion);

    internal static string? LatestDllPath(string revitVersion)
    {
        string folder = Folder(revitVersion);
        if (!Directory.Exists(folder)) return null;
        // El nombre lleva la fecha y hora (yyyyMMdd_HHmmss): el orden alfabético es el cronológico.
        return Directory.GetFiles(folder, "KaikenDev_*.dll")
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .LastOrDefault();
    }

    /// <summary>Carga (o reutiliza, si ya estaba) la DLL dev más reciente para esta versión de Revit.</summary>
    internal static Assembly LoadLatest(UIApplication app)
    {
        string version = app.Application.VersionNumber;
        string path = LatestDllPath(version)
            ?? throw new FileNotFoundException(
                $"No hay builds de prueba en {Folder(version)}.\nCompila uno con: tools\\dev-build.ps1 -RevitVersion {version}");

        if (Loaded.TryGetValue(path, out var cached)) return cached;

        var context = AssemblyLoadContext.GetLoadContext(typeof(App).Assembly) ?? AssemblyLoadContext.Default;
        Assembly asm = context.LoadFromAssemblyPath(path);
        Loaded[path] = asm;
        App.BridgeLog($"dev: cargado {Path.GetFileName(path)} en el contexto '{context.Name}'");
        return asm;
    }

    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    /// <summary>Primero lo de la carpeta Dev\, después el resto, por nombre.</summary>
    private static List<Type> Sorted(IEnumerable<Type> types) => types
        .OrderBy(t => t.Namespace == DevNamespace ? 0 : 1)
        .ThenBy(t => t.FullName, StringComparer.Ordinal)
        .ToList();

    /// <summary>Comandos de la DLL dev (todos los de Kaiken, con la versión nueva, más los de prueba).</summary>
    internal static List<Type> Commands(Assembly asm) => Sorted(SafeTypes(asm).Where(t =>
        t.IsClass && !t.IsAbstract
        && typeof(IExternalCommand).IsAssignableFrom(t)
        && t.GetConstructor(Type.EmptyTypes) != null
        && t.FullName != typeof(DevCommand).FullName));

    /// <summary>Contenidos para el panel acoplable: FrameworkElement (no Window) con constructor () o (UIApplication).</summary>
    internal static List<Type> Panels(Assembly asm) => Sorted(SafeTypes(asm).Where(t =>
        t.IsClass && !t.IsAbstract
        && typeof(FrameworkElement).IsAssignableFrom(t)
        && !typeof(Window).IsAssignableFrom(t)
        && (t.GetConstructor(Type.EmptyTypes) != null || t.GetConstructor(new[] { typeof(UIApplication) }) != null)));

    /// <summary>Métodos estáticos públicos de la carpeta Dev\ que el puente puede invocar.</summary>
    internal static List<MethodInfo> Methods(Assembly asm) => SafeTypes(asm)
        .Where(t => t.Namespace == DevNamespace)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        .Where(m => !m.IsSpecialName && m.GetParameters().All(p => IsSupportedParameter(p.ParameterType)))
        .OrderBy(m => m.DeclaringType!.FullName).ThenBy(m => m.Name)
        .ToList();

    private static bool IsSupportedParameter(Type t) =>
        t == typeof(UIApplication) || t == typeof(UIDocument) || t == typeof(Document) || t == typeof(JsonElement);

    private static string Signature(MethodInfo m) =>
        $"{m.DeclaringType!.FullName}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})";

    /// <summary>Busca un tipo por nombre completo o, si no es ambiguo, por nombre corto.</summary>
    private static Type FindType(Assembly asm, string name)
    {
        var types = SafeTypes(asm).ToList();
        var exact = types.FirstOrDefault(t => t.FullName == name);
        if (exact != null) return exact;
        var byName = types.Where(t => t.Name == name).ToList();
        return byName.Count switch
        {
            1 => byName[0],
            0 => throw new ArgumentException($"No existe el tipo '{name}' en {asm.GetName().Name}."),
            _ => throw new ArgumentException($"'{name}' es ambiguo: {string.Join(", ", byName.Select(t => t.FullName))}."),
        };
    }

    /// <summary>Ejecuta un comando de la DLL dev con el ExternalCommandData real del botón Dev.</summary>
    internal static Result RunCommand(Type type, ExternalCommandData data, ref string message, ElementSet elements)
    {
        var command = (IExternalCommand)Activator.CreateInstance(type)!;
        App.BridgeLog($"dev: ejecutando {type.FullName} ({type.Assembly.GetName().Name})");
        return command.Execute(data, ref message, elements);
    }

    /// <summary>Reemplaza el contenido del panel acoplable por una instancia nueva de <paramref name="type"/> y lo muestra.</summary>
    internal static string ShowInPane(UIApplication app, Type type)
    {
        if (_pane == null)
            throw new InvalidOperationException("El panel Kaiken Dev no está registrado: activa devMode en settings.json y reinicia Revit.");

        // Se crea dentro de un contexto de la API (botón Dev o puente): el constructor puede crear
        // su ExternalEvent para lo que haga después desde sus botones.
        var content = (FrameworkElement)(type.GetConstructor(new[] { typeof(UIApplication) }) != null
            ? Activator.CreateInstance(type, app)!
            : Activator.CreateInstance(type)!);

        string label = $"{type.FullName} · {type.Assembly.GetName().Name}";
        _pane.SetContent(content, label);
        app.GetDockablePane(PaneId).Show();
        App.BridgeLog($"dev: panel muestra {label}");
        return label;
    }

    internal static void ShowError(string what, Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
        App.BridgeLog($"dev: error en {what}: {ex}");
        new TaskDialog(Title)
        {
            MainInstruction = $"Error en {what}",
            MainContent = ex.Message,
            ExpandedContent = ex.ToString(),
        }.Show();
    }

    // --- Puente en vivo (operaciones dev_* de LiveBridgeOperations) ---

    /// <summary>Corre una operación dev del puente: comprueba que el modo esté activo y devuelve los errores con su traza.</summary>
    internal static string Bridge(Func<object> operation)
    {
        if (!KaikenSettings.Current.DevMode.Enabled)
            return JsonSerializer.Serialize(new { error = "Modo desarrollo desactivado: pon \"devMode\": { \"enabled\": true } en %APPDATA%\\Kaiken\\settings.json." });
        try
        {
            return JsonSerializer.Serialize(operation(), JsonOptions);
        }
        catch (Exception ex)
        {
            if (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
            App.BridgeLog($"dev: error en el puente: {ex}");
            return JsonSerializer.Serialize(new { error = ex.Message, detail = ex.ToString() });
        }
    }

    internal static object List(UIApplication app)
    {
        Assembly asm = LoadLatest(app);
        return new
        {
            ok = true,
            folder = Folder(app.Application.VersionNumber),
            assembly = asm.GetName().Name,
            loaded = Loaded.Keys.Select(Path.GetFileName).ToList(),
            paneRegistered = _pane != null,
            commands = Commands(asm).Select(t => t.FullName).ToList(),
            panels = Panels(asm).Select(t => t.FullName).ToList(),
            methods = Methods(asm).Select(Signature).ToList(),
        };
    }

    /// <summary>
    /// args: { "method": "Tipo.Metodo", "args": {...} } para un método estático (parámetros
    /// UIApplication, UIDocument, Document y/o JsonElement, que recibe "args"), o
    /// { "command": "Tipo" } para un IExternalCommand. Lo que devuelve el método se serializa a
    /// JSON (un string con JSON válido se pasa tal cual); conviene devolver objetos simples, no
    /// elementos de Revit.
    /// </summary>
    internal static object Invoke(UIApplication app, JsonElement args)
    {
        Assembly asm = LoadLatest(app);
        string? command = GetString(args, "command");
        string? method = GetString(args, "method");
        JsonElement methodArgs = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("args", out var a)
            ? a.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();

        if (!string.IsNullOrWhiteSpace(command))
        {
            Type type = FindType(asm, command!);
            if (!typeof(IExternalCommand).IsAssignableFrom(type))
                throw new ArgumentException($"{type.FullName} no es un IExternalCommand.");

            // ExternalCommandData no tiene constructor público; sus propiedades sí se pueden asignar.
            var data = (ExternalCommandData)Activator.CreateInstance(typeof(ExternalCommandData), nonPublic: true)!;
            data.Application = app;
            if (app.ActiveUIDocument?.ActiveView is View view) data.View = view;
            data.JournalData = new Dictionary<string, string>();
            string message = "";
            Result result = RunCommand(type, data, ref message, new ElementSet());
            return new { ok = true, assembly = asm.GetName().Name, command = type.FullName, result = result.ToString(), message };
        }

        if (!string.IsNullOrWhiteSpace(method))
        {
            int dot = method!.LastIndexOf('.');
            if (dot <= 0) throw new ArgumentException("'method' debe ser Tipo.Metodo, ej. DevSamples.ViewportPositions.");
            Type type = FindType(asm, method[..dot]);
            string name = method[(dot + 1)..];
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name).ToList();
            if (candidates.Count != 1)
                throw new ArgumentException(candidates.Count == 0
                    ? $"{type.FullName} no tiene un método estático público '{name}'."
                    : $"{type.FullName}.{name} tiene sobrecargas; deja una sola.");

            MethodInfo m = candidates[0];
            object?[] parameters = m.GetParameters().Select(p => ResolveParameter(p.ParameterType, app, methodArgs)).ToArray();
            App.BridgeLog($"dev: invocando {Signature(m)} ({asm.GetName().Name})");
            object? value = m.Invoke(null, parameters);
            return new { ok = true, assembly = asm.GetName().Name, method = Signature(m), result = Shape(value) };
        }

        throw new ArgumentException("Indica 'method' (Tipo.Metodo estático) o 'command' (un IExternalCommand).");
    }

    internal static object ShowPanel(UIApplication app, JsonElement args)
    {
        Assembly asm = LoadLatest(app);
        string name = GetString(args, "type") ?? throw new ArgumentException("Indica 'type' (el FrameworkElement a mostrar).");
        Type type = FindType(asm, name);
        if (!Panels(asm).Contains(type))
            throw new ArgumentException($"{type.FullName} no sirve para el panel: debe ser un FrameworkElement (no Window) con constructor () o (UIApplication).");
        return new { ok = true, shown = ShowInPane(app, type) };
    }

    private static object? ResolveParameter(Type t, UIApplication app, JsonElement args)
    {
        if (t == typeof(UIApplication)) return app;
        if (t == typeof(JsonElement)) return args;
        if (t == typeof(UIDocument)) return app.ActiveUIDocument ?? throw new InvalidOperationException("No hay ningún documento abierto en Revit.");
        if (t == typeof(Document)) return app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No hay ningún documento abierto en Revit.");
        throw new NotSupportedException($"Parámetro no soportado: {t.Name}. Usa UIApplication, UIDocument, Document o JsonElement.");
    }

    /// <summary>Un string con JSON válido se devuelve como JSON, no como texto escapado.</summary>
    private static object? Shape(object? value)
    {
        if (value is not string s) return value;
        try
        {
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return s; }
    }

    private static string? GetString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>
/// Botón Dev de la cinta: carga el build de prueba más reciente, lista sus comandos y paneles, y
/// ejecuta el elegido con el ExternalCommandData real (o lo muestra en el panel Kaiken Dev).
/// Manual: el comando elegido abre sus propias transacciones, como desde su botón.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class DevCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication app = commandData.Application;
        Assembly asm;
        try
        {
            asm = DevMode.LoadLatest(app);
        }
        catch (Exception ex)
        {
            DevMode.ShowError("la carga del build de prueba", ex);
            return Result.Cancelled;
        }

        var win = new DevWindow(asm, DevMode.Commands(asm), DevMode.Panels(asm), DevMode.LastChoice);
        new WindowInteropHelper(win).Owner = app.MainWindowHandle;
        if (win.ShowDialog() != true || win.Selected == null) return Result.Cancelled;

        Type selected = win.Selected;
        DevMode.LastChoice = selected.FullName;
        try
        {
            if (typeof(IExternalCommand).IsAssignableFrom(selected))
                return DevMode.RunCommand(selected, commandData, ref message, elements);

            DevMode.ShowInPane(app, selected);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            DevMode.ShowError(selected.FullName ?? selected.Name, ex);
            return Result.Cancelled;
        }
    }
}

/// <summary>
/// Panel acoplable contenedor "Kaiken Dev". Revit solo deja registrar paneles en OnStartup, así que
/// Kaiken instalado registra este (vacío) y el modo desarrollo le cambia el contenido por un
/// FrameworkElement de la DLL dev, cuantas veces haga falta.
/// </summary>
internal sealed class DevPane : IDockablePaneProvider
{
    private readonly DockPanel _root = new() { Background = Brushes.White };
    private readonly TextBlock _header = new()
    {
        Margin = new Thickness(6, 4, 6, 4),
        Foreground = Brushes.Gray,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly ContentControl _content = new();

    public DevPane()
    {
        DockPanel.SetDock(_header, Dock.Top);
        _root.Children.Add(_header);
        _root.Children.Add(_content);
        _header.Text = "Panel de desarrollo";
        _content.Content = new TextBlock
        {
            Margin = new Thickness(8),
            TextWrapping = TextWrapping.Wrap,
            Text = "Aquí se muestran los paneles del build de prueba: elige uno con el botón Dev de la cinta, " +
                   "o con la operación dev_show_panel del puente en vivo.",
        };
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _root;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right, MinimumWidth = 250 };
        data.VisibleByDefault = false;
    }

    internal void SetContent(FrameworkElement content, string label)
    {
        // El contenido anterior (de un build viejo) se libera: ahí desregistra sus eventos e IUpdater.
        if (_content.Content is IDisposable previous)
        {
            try { previous.Dispose(); }
            catch (Exception ex) { App.BridgeLog($"dev: Dispose del panel anterior falló: {ex.Message}"); }
        }
        _content.Content = content;
        _header.Text = label;
        _header.ToolTip = label;
    }
}
