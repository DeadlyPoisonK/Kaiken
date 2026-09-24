using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Kaiken;

public class CadBlockInstanceInfo
{
    public string BlockName { get; set; } = "";
    public string LayerName { get; set; } = "";
    public XYZ Position { get; set; } = XYZ.Zero; // Coordenadas en pies
    public double RotationDeg { get; set; } = 0.0;
    public bool IsMirrored { get; set; } = false;
}

public static class DwgToBlocksHelpers
{
    public const double MetersToFeet = 3.280839895;

    public static List<ImportInstance> GetImportInstances(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ImportInstance))
            .Cast<ImportInstance>()
            .Where(i => i.Category != null)
            .ToList();
    }

    public static List<FamilySymbol> GetAllFamilySymbols(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .OrderBy(s => s.FamilyName)
            .ThenBy(s => s.Name)
            .ToList();
    }

    public static List<Level> GetLevels(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();
    }

    /// <summary>
    /// Escanea la geometría de una ImportInstance para extraer todas las referencias a bloques de CAD.
    /// Revit convierte automáticamente la geometría del DWG a pies al importar/vincular el plano.
    /// </summary>
    public static List<CadBlockInstanceInfo> ExtractBlocksFromImport(ImportInstance importElem)
    {
        var result = new List<CadBlockInstanceInfo>();
        if (importElem == null) return result;

        Document doc = importElem.Document;
        Options opt = new Options
        {
            ComputeReferences = false,
            DetailLevel = ViewDetailLevel.Fine
        };

        GeometryElement? mainGeoElem = importElem.get_Geometry(opt);
        if (mainGeoElem == null) return result;

        foreach (GeometryObject mainGeoObj in mainGeoElem)
        {
            if (mainGeoObj is GeometryInstance mainInst)
            {
                GeometryElement? symbolGeo = mainInst.SymbolGeometry;
                if (symbolGeo == null) continue;

                CollectBlocksRecursive(doc, symbolGeo, mainInst.Transform, result, 0);
            }
        }

        return result;
    }

    /// <summary>
    /// Recorre recursivamente instancias de bloque CAD anidadas (bloque dentro de bloque).
    /// Revit expone cada nivel de INSERT como un GeometryInstance separado; si solo se
    /// inspecciona el primer nivel, los bloques anidados dos o más niveles (comunes en DWG
    /// con bloques dinámicos o símbolos copiados dentro de otro bloque contenedor) se pierden.
    /// </summary>
    private static void CollectBlocksRecursive(
        Document doc,
        GeometryElement geoElem,
        Transform accumulatedTransform,
        List<CadBlockInstanceInfo> result,
        int depth)
    {
        if (depth > 16) return; // salvaguarda contra referencias circulares

        foreach (GeometryObject subObj in geoElem)
        {
            if (subObj is not GeometryInstance blockInst) continue;

            Transform worldTransform = accumulatedTransform.Multiply(blockInst.Transform);

            // 1. Obtener capa del bloque
            string tempLayerName = "Sin Capa";
            GeometryElement? blockSymbolGeo = blockInst.SymbolGeometry;
            if (blockSymbolGeo != null)
            {
                foreach (GeometryObject geo in blockSymbolGeo)
                {
                    ElementId styleId = geo.GraphicsStyleId;
                    if (styleId != ElementId.InvalidElementId)
                    {
                        if (doc.GetElement(styleId) is GraphicsStyle style && style.GraphicsStyleCategory != null)
                        {
                            string styleName = style.GraphicsStyleCategory.Name;
                            if (!string.IsNullOrWhiteSpace(styleName))
                            {
                                tempLayerName = styleName;
                                break;
                            }
                        }
                    }
                }
            }

            // 2. Calcular Posición Exacta en Coordenadas Modelo (Revit convirtió las unidades DWG a pies en la inserción)
            XYZ worldPoint = worldTransform.Origin;

            // 3. Rotación y Anti-Espejado (Anti-Mirror)
            XYZ xVec = worldTransform.BasisX;
            XYZ yVec = worldTransform.BasisY;
            XYZ zGlobal = XYZ.BasisZ;

            double angleRad = Math.Atan2(xVec.Y, xVec.X);
            double angleDeg = angleRad * (180.0 / Math.PI);

            // Detección de espejo (producto cruz apuntando hacia abajo)
            XYZ crossProd = xVec.CrossProduct(yVec);
            bool isMirrored = crossProd.DotProduct(zGlobal) < 0;
            if (isMirrored)
            {
                angleDeg += 180.0;
            }
            angleDeg = (angleDeg % 360.0 + 360.0) % 360.0;

            // 4. Nombre del Bloque CAD
            string blockName = "Bloque Desconocido";
            ElementId symbolId = ElementId.InvalidElementId;
            try
            {
                symbolId = blockInst.GetSymbolGeometryId()?.SymbolId ?? ElementId.InvalidElementId;
            }
            catch
            {
                // Compatibilidad versiones antiguas
            }

            if (symbolId != ElementId.InvalidElementId)
            {
                Element sym = doc.GetElement(symbolId);
                if (sym != null)
                {
                    blockName = sym.Name;
                    if (blockName.Contains(".dwg."))
                    {
                        blockName = blockName.Split(new[] { ".dwg." }, StringSplitOptions.None).Last();
                    }
                }
            }

            result.Add(new CadBlockInstanceInfo
            {
                BlockName = blockName,
                LayerName = tempLayerName,
                Position = worldPoint,
                RotationDeg = Math.Round(angleDeg, 3),
                IsMirrored = isMirrored
            });

            // 5. Recursión: bajar un nivel más por si este bloque contiene otros bloques anidados
            if (blockSymbolGeo != null)
            {
                CollectBlocksRecursive(doc, blockSymbolGeo, worldTransform, result, depth + 1);
            }
        }
    }
}
