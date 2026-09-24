using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace Kaiken;

/// <summary>
/// Lógica de apoyo para cambiar el Tipo de Tubería (lo que en la cinta de Revit se ve
/// como "Familia" en el selector de tipo, aunque técnicamente es un PipeType de familia
/// de sistema) sin tocar el diámetro ni ningún otro parámetro de la tubería.
/// </summary>
public static class ChangePipeTypeHelpers
{
    /// <summary>Todos los Tipos de Tubería del proyecto, ordenados por nombre.</summary>
    public static List<PipeType> GetAllPipeTypes(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(PipeType))
            .Cast<PipeType>()
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Todas las tuberías visibles en la vista dada.</summary>
    public static List<Pipe> GetPipesInView(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(Pipe))
            .Cast<Pipe>()
            .ToList();

    /// <summary>Diámetro nominal de la tubería redondeado al milímetro, para agrupar/filtrar.</summary>
    public static double DiameterMm(Pipe pipe) =>
        Math.Round(UnitUtils.ConvertFromInternalUnits(pipe.Diameter, UnitTypeId.Millimeters), 0);

    /// <summary>
    /// Intenta preseleccionar el Tipo destino más probable para un diámetro dado, buscando
    /// el número de milímetros como palabra dentro del nombre del tipo (ej. "PVC.U 110mm").
    /// Es solo una ayuda: el usuario puede elegir cualquier otro tipo en el combo.
    /// </summary>
    public static PipeType? GuessTargetType(List<PipeType> allTypes, double diameterMm, string? sourceTypeName = null)
    {
        string needle = diameterMm.ToString("0");

        var candidates = allTypes.Where(t => t.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // Si hay varios candidatos (ej. "PVC.U 110mm" y "V.PVC.110mm"), preferir el que
        // comparte más prefijo de nombre con el tipo de origen (mismo "apellido" de familia).
        if (!string.IsNullOrEmpty(sourceTypeName))
        {
            string sourcePrefix = sourceTypeName.Split('.', '_', ' ').FirstOrDefault() ?? "";
            var byPrefix = candidates.FirstOrDefault(t =>
                t.Name.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase));
            if (byPrefix != null) return byPrefix;
        }

        return candidates.OrderBy(t => t.Name.Length).First();
    }
}
