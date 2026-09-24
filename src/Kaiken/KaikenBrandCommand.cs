using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Kaiken;

/// <summary>
/// Comando "vacío" que respalda el botón de marca Kaiken en la cinta. El botón
/// queda deshabilitado (ver App.cs), así que esto nunca se ejecuta en la práctica —
/// existe solo porque un PushButtonData necesita apuntar a una clase IExternalCommand válida.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class KaikenBrandCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        return Result.Succeeded;
    }
}
