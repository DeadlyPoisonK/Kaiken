using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Kaiken;

/// <summary>Una inserción de bloque (INSERT) con sus atributos.</summary>
public class BlockInsert
{
    public string Block = "";
    public string Layer = "";
    public double X, Y, Z;
    public double RotationDeg;
    public double ScaleX = 1.0, ScaleY = 1.0;
    public Dictionary<string, string> Attribs = new(StringComparer.OrdinalIgnoreCase);

    public double? AlturaMeters()
    {
        foreach (var key in new[] { "ALTURA", "ALT", "H" })
            if (Attribs.TryGetValue(key, out var s) &&
                double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
        return null;
    }
    public string Tipo() => Attribs.TryGetValue("TIPOALIMENTACIÓN", out var t) ? t
                          : Attribs.TryGetValue("TIPOALIMENTACION", out var t2) ? t2 : "";
    public string Eq() => Attribs.TryGetValue("EQ.ALIMENTADO", out var e) ? e : "";
}

/// <summary>Estilo de texto CAD leído de la tabla STYLE del DXF.</summary>
public class DxfTextStyle
{
    public string Name     = "";
    public string FontFile = "";  // ej. "romans.shx" o "Arial.ttf"
    public double HeightCad;      // alto fijo (0 = variable por entidad)
}

/// <summary>Un texto (TEXT/MTEXT): capa, posición, contenido y metadatos de estilo.</summary>
public class DxfText
{
    public string Layer      = "";
    public double X, Y;
    public string Text       = "";
    public double HeightCad;      // código 40: alto en unidades DXF
    public double RotationDeg;    // código 50: rotación en grados
    public string StyleName  = ""; // código 7: nombre del estilo de texto
}

/// <summary>Resultado del parseo del DXF en una sola pasada.</summary>
public class DxfData
{
    public List<BlockInsert> Inserts = new();
    public List<DxfText>     Texts   = new();
    public int    InsUnits    = 0;
    public double FeetPerUnit = 1.0 / 304.8;
    /// <summary>Estilos de texto del DXF (tabla STYLE), clave = nombre del estilo.</summary>
    public Dictionary<string, DxfTextStyle> TextStyles = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Parser de DXF ASCII sin dependencias, en una sola pasada por streaming
/// (bajo consumo de memoria, apto para DXF de cientos de MB). Extrae INSERT
/// (con ATTRIB), TEXT/MTEXT y $INSUNITS.
/// </summary>
public static class EnchufesDxf
{
    public static DxfData Read(string dxfPath)
    {
        var data = new DxfData();
        using var sr = new StreamReader(dxfPath);

        string? l1, l2;
        string section  = "";
        string mode     = "";  // insert | attrib | text | mtext
        BlockInsert?   curIns   = null;
        DxfText?       curTxt   = null;
        DxfTextStyle?  curStyle = null;   // estilo activo mientras se parsea TABLES
        var mtextBuf = new StringBuilder();
        string attTag = "", attVal = "";
        string headerVar = "";

        void FlushStyle()
        {
            if (curStyle != null && curStyle.Name.Length > 0)
                data.TextStyles[curStyle.Name] = curStyle;
            curStyle = null;
        }

        void FlushAttrib()
        {
            if (curIns != null && attTag.Length > 0) curIns.Attribs[attTag] = attVal;
            attTag = ""; attVal = "";
        }
        void FlushMText()
        {
            if (curTxt != null) { curTxt.Text = CleanMText(mtextBuf.ToString()); data.Texts.Add(curTxt); curTxt = null; }
            mtextBuf.Clear();
        }
        void CloseEntity()
        {
            if (mode == "attrib") FlushAttrib();
            else if (mode == "mtext") FlushMText();
            else if (mode == "text" && curTxt != null) { data.Texts.Add(curTxt); curTxt = null; }
        }

        while ((l1 = sr.ReadLine()) != null)
        {
            l2 = sr.ReadLine();
            if (l2 == null) break;
            if (!int.TryParse(l1.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                continue;
            string v = l2.Trim();

            if (code == 2 && v == "HEADER")   section = "HEADER";
            if (code == 2 && v == "TABLES")   section = "TABLES";
            if (code == 2 && v == "ENTITIES") section = "ENTITIES";

            // --- HEADER: $INSUNITS ---
            if (section == "HEADER")
            {
                if (code == 9) headerVar = v;
                else if (code == 70 && headerVar == "$INSUNITS")
                {
                    int.TryParse(v, out data.InsUnits);
                    headerVar = "";
                }
                if (!(code == 0 && v == "ENDSEC")) continue;
            }

            // ── TABLES: parsear la tabla STYLE para obtener fuentes ──────────────
            if (section == "TABLES")
            {
                if (code == 0 && v == "ENDSEC") { FlushStyle(); section = ""; continue; }
                if (code == 0 && v == "STYLE")  { FlushStyle(); curStyle = new DxfTextStyle(); continue; }
                if (code == 0)                  { FlushStyle(); curStyle = null; continue; }
                if (curStyle != null)
                {
                    if      (code == 2)  curStyle.Name     = v;
                    else if (code == 4)  curStyle.FontFile = v;          // fuente primaria
                    else if (code == 40) curStyle.HeightCad = ParseD(l2);
                }
                continue;
            }

            if (section != "ENTITIES") continue;

            if (code == 0)
            {
                CloseEntity();
                switch (v)
                {
                    case "INSERT": curIns = new BlockInsert(); data.Inserts.Add(curIns); mode = "insert"; break;
                    case "ATTRIB": mode = "attrib"; attTag = ""; attVal = ""; break;
                    case "TEXT": curTxt = new DxfText(); mode = "text"; break;
                    case "MTEXT": curTxt = new DxfText(); mtextBuf.Clear(); mode = "mtext"; break;
                    case "SEQEND": mode = ""; curIns = null; break;
                    case "ENDSEC": section = ""; mode = ""; curIns = null; curTxt = null; break;
                    default: mode = ""; curIns = null; break;
                }
                continue;
            }

            switch (mode)
            {
                case "insert":
                    if (curIns == null) break;
                    switch (code)
                    {
                        case 8: curIns.Layer = l2.Trim(); break;
                        case 2: curIns.Block = l2.Trim(); break;
                        case 10: curIns.X = ParseD(l2); break;
                        case 20: curIns.Y = ParseD(l2); break;
                        case 30: curIns.Z = ParseD(l2); break;
                        case 50: curIns.RotationDeg = ParseD(l2); break;
                        case 41: curIns.ScaleX = ParseD(l2); break;
                        case 42: curIns.ScaleY = ParseD(l2); break;
                    }
                    break;
                case "attrib":
                    if (code == 1) attVal = l2.Trim();
                    else if (code == 2) attTag = l2.Trim();
                    break;
                case "text":
                    if (curTxt == null) break;
                    if      (code == 8)  curTxt.Layer      = l2.Trim();
                    else if (code == 10) curTxt.X          = ParseD(l2);
                    else if (code == 20) curTxt.Y          = ParseD(l2);
                    else if (code == 1)  curTxt.Text       = l2.Trim();
                    else if (code == 7)  curTxt.StyleName  = v;          // estilo
                    else if (code == 40) curTxt.HeightCad  = ParseD(l2); // alto
                    else if (code == 50) curTxt.RotationDeg = ParseD(l2);// rotación
                    break;
                case "mtext":
                    if (curTxt == null) break;
                    if      (code == 8)  curTxt.Layer      = l2.Trim();
                    else if (code == 10) curTxt.X          = ParseD(l2);
                    else if (code == 20) curTxt.Y          = ParseD(l2);
                    else if (code == 3)  mtextBuf.Append(l2);            // fragmentos largos
                    else if (code == 1)  mtextBuf.Append(l2);            // fragmento final
                    else if (code == 7)  curTxt.StyleName  = v;          // estilo
                    else if (code == 40) curTxt.HeightCad  = ParseD(l2); // alto de referencia
                    else if (code == 50) curTxt.RotationDeg = ParseD(l2);// rotación
                    break;
            }
        }
        CloseEntity();

        data.FeetPerUnit = data.InsUnits switch
        {
            1 => 1.0 / 12.0, 2 => 1.0, 4 => 1.0 / 304.8, 5 => 1.0 / 30.48, 6 => 3.280839895,
            _ => 1.0 / 304.8,
        };
        return data;
    }

    /// <summary>Quita códigos de formato de MTEXT (\A1; \f...; {}, \P -> espacio).</summary>
    public static string CleanMText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\\P", "\n");
        s = Regex.Replace(s, @"\\[A-Za-z][^;\\]*;", "");   // \A1;  \fArial|b0; etc.
        s = s.Replace("{", "").Replace("}", "").Replace("\\", "");
        return s.Trim();
    }

    private static double ParseD(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
}
