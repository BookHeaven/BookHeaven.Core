using System.Globalization;
using System.Text.RegularExpressions;

namespace BookHeaven.Core.DOM.Mini;

/// <summary>
/// The viewport/font context a length resolves against. Plain data (no AngleSharp
/// <c>IRenderDevice</c>): positive dimensions and font size, clamped by the engine.
/// </summary>
public readonly record struct MiniRenderDevice(float ViewPortWidth, float ViewPortHeight, float FontSize);

/// <summary>
/// Resolves a CSS length string to pixels: px/em/rem/%/pt/in/vh/vw/vmin/vmax,
/// <c>calc()</c>, <c>min()</c>/<c>max()</c>/<c>clamp()</c>, and unitless numbers
/// (0 → 0px; other unitless values return null so callers can treat them as
/// multipliers, e.g. line-height). <c>var()</c> is resolved by the style resolver
/// BEFORE this parser sees the value, so it is not handled here.
/// </summary>
public static partial class MiniLengthParser
{
    public static float? ParseLengthToPx(string? s, MiniRenderDevice device, float reference, float? emReference = null)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cur = s.Trim();

        if (string.Equals(cur, "auto", StringComparison.OrdinalIgnoreCase) || string.Equals(cur, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        // max() / min() / clamp() over comma-separated arguments.
        if (cur.Contains("max(", StringComparison.OrdinalIgnoreCase))
            return EvaluateFunction(cur, "max", device, reference, emReference, Math.Max);
        if (cur.Contains("min(", StringComparison.OrdinalIgnoreCase))
            return EvaluateFunction(cur, "min", device, reference, emReference, Math.Min);
        if (cur.Contains("clamp(", StringComparison.OrdinalIgnoreCase))
            return EvaluateClamp(cur, device, reference, emReference);

        if (cur.Contains("calc(", StringComparison.OrdinalIgnoreCase))
        {
            var inner = ExtractFunctionInner(cur, "calc");
            if (inner.Length > 0)
                return EvaluateCalcExpression(inner, device, reference, emReference);
        }

        if (cur.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
            return TryNumber(cur, 3, out var v) ? v * device.FontSize : null;

        if (cur.EndsWith("em", StringComparison.OrdinalIgnoreCase))
            return TryNumber(cur, 2, out var v2) ? v2 * (emReference ?? device.FontSize) : null;

        if (cur.EndsWith('%'))
            return TryNumber(cur, 1, out var pct) ? (pct / 100f) * reference : null;

        if (cur.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            return TryNumber(cur, 2, out var v3) ? v3 : null;

        // Print units: 1in = 96px, 1pt = 96/72 px. The digit/dot guard keeps tokens
        // like "9min" from matching the "in" suffix.
        if (cur.Length > 2 && (char.IsDigit(cur[^3]) || cur[^3] == '.') &&
            (cur.EndsWith("pt", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("in", StringComparison.OrdinalIgnoreCase)))
        {
            var unit = cur[^2..].ToLowerInvariant();
            if (TryNumber(cur, 2, out var v4))
                return unit == "pt" ? v4 * (96f / 72f) : v4 * 96f;
            return null;
        }

        if (cur.EndsWith("vmin", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("vmax", StringComparison.OrdinalIgnoreCase)
            || cur.EndsWith("vh", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("vw", StringComparison.OrdinalIgnoreCase))
        {
            var unit = cur[^2..].ToLowerInvariant();
            if (!TryNumber(cur, unit.Length, out var v5))
                return null;
            return unit switch
            {
                "vh" => (v5 / 100f) * device.ViewPortHeight,
                "vw" => (v5 / 100f) * device.ViewPortWidth,
                "vmin" => (v5 / 100f) * Math.Min(device.ViewPortWidth, device.ViewPortHeight),
                _ => (v5 / 100f) * Math.Max(device.ViewPortWidth, device.ViewPortHeight)
            };
        }

        // Unitless: only 0 is a valid length; other unitless values are multipliers.
        if (float.TryParse(cur, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw) && raw == 0f)
            return 0f;
        return null;
    }

    private static bool TryNumber(string s, int suffixLength, out float value)
    {
        value = 0f;
        if (s.Length <= suffixLength) return false;
        var num = s[..^suffixLength].Trim();
        return float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static float? EvaluateFunction(string cur, string name, MiniRenderDevice device, float reference, float? emReference, Func<float, float, float> combine)
    {
        var inner = ExtractFunctionInner(cur, name);
        if (inner.Length == 0) return null;
        float? best = null;
        foreach (var part in SplitTopLevelCommas(inner))
        {
            var v = ParseLengthToPx(part, device, reference, emReference);
            if (v.HasValue) best = best.HasValue ? combine(best.Value, v.Value) : v.Value;
        }
        return best;
    }

    private static float? EvaluateClamp(string cur, MiniRenderDevice device, float reference, float? emReference)
    {
        var inner = ExtractFunctionInner(cur, "clamp");
        if (inner.Length == 0) return null;
        var parts = SplitTopLevelCommas(inner);
        if (parts.Count != 3) return null;
        var minv = ParseLengthToPx(parts[0], device, reference, emReference);
        var valv = ParseLengthToPx(parts[1], device, reference, emReference);
        var maxv = ParseLengthToPx(parts[2], device, reference, emReference);
        if (!valv.HasValue) return null;
        var v = valv.Value;
        if (minv.HasValue) v = Math.Max(v, minv.Value);
        if (maxv.HasValue) v = Math.Min(v, maxv.Value);
        return v;
    }

    /// <summary>Extracts the argument text between the parentheses of <c>name(...)</c>.</summary>
    private static string ExtractFunctionInner(string s, string functionName)
    {
        var idx = s.IndexOf(functionName + "(", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;
        var start = s.IndexOf('(', idx) + 1;
        var depth = 1;
        for (var i = start; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return s[start..i].Trim();
            }
        }
        return string.Empty;
    }

    private static List<string> SplitTopLevelCommas(string s)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return parts;
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (depth == 0 && s[i] == ',')
            {
                parts.Add(s[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(s[start..].Trim());
        return parts;
    }

    /// <summary>
    /// Evaluates a <c>calc()</c> body to a length. A unitless result is not a valid
    /// length (only 0 is), so it returns null — callers use <see cref="ParseUnitless"/>
    /// for multiplier values such as line-height.
    /// </summary>
    private static float? EvaluateCalcExpression(string expr, MiniRenderDevice device, float reference, float? emReference)
    {
        var result = EvaluateCalc(expr, device, reference, emReference);
        if (result is not { } final) return null;
        if (!final.IsLength && final.Value != 0) return null;
        return (float)final.Value;
    }

    /// <summary>
    /// Parses a unitless number (e.g. a line-height multiplier): plain numbers,
    /// unitless <c>calc()</c>, and unitless <c>min()</c>/<c>max()</c>/<c>clamp()</c>.
    /// Returns null for lengths and unparseable values.
    /// </summary>
    public static float? ParseUnitless(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cur = s.Trim();

        if (float.TryParse(cur, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
            return plain;

        if (cur.Contains("calc(", StringComparison.OrdinalIgnoreCase))
        {
            var inner = ExtractFunctionInner(cur, "calc");
            if (inner.Length == 0) return null;
            var r = EvaluateCalc(inner, default, 0, null);
            return r is { IsLength: false } u ? (float)u.Value : null;
        }

        if (cur.Contains("max(", StringComparison.OrdinalIgnoreCase))
            return EvaluateUnitlessFunction(cur, "max", Math.Max);
        if (cur.Contains("min(", StringComparison.OrdinalIgnoreCase))
            return EvaluateUnitlessFunction(cur, "min", Math.Min);
        if (cur.Contains("clamp(", StringComparison.OrdinalIgnoreCase))
        {
            var inner = ExtractFunctionInner(cur, "clamp");
            if (inner.Length == 0) return null;
            var parts = SplitTopLevelCommas(inner);
            if (parts.Count != 3) return null;
            var val = ParseUnitless(parts[1]);
            if (val is null) return null;
            var v = val.Value;
            var minv = ParseUnitless(parts[0]);
            if (minv.HasValue) v = Math.Max(v, minv.Value);
            var maxv = ParseUnitless(parts[2]);
            if (maxv.HasValue) v = Math.Min(v, maxv.Value);
            return v;
        }
        return null;
    }

    private static float? EvaluateUnitlessFunction(string cur, string name, Func<float, float, float> combine)
    {
        var inner = ExtractFunctionInner(cur, name);
        if (inner.Length == 0) return null;
        float? best = null;
        foreach (var part in SplitTopLevelCommas(inner))
        {
            var v = ParseUnitless(part);
            if (v.HasValue) best = best.HasValue ? combine(best.Value, v.Value) : v.Value;
        }
        return best;
    }

    /// <summary>
    /// Evaluates a <c>calc()</c> body with a shunting-yard evaluator. Lengths and
    /// unitless numbers are tracked separately so mixed-unit arithmetic (e.g.
    /// <c>100% - 2em</c>) is rejected, matching CSS.
    /// </summary>
    private static (double Value, bool IsLength)? EvaluateCalc(string expr, MiniRenderDevice device, float reference, float? emReference)
    {
        if (string.IsNullOrWhiteSpace(expr)) return null;

        var tokens = new List<string>();
        for (var i = 0; i < expr.Length;)
        {
            if (char.IsWhiteSpace(expr[i])) { i++; continue; }
            var c = expr[i];
            if (c is '+' or '-' or '*' or '/' or '(' or ')')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }
            var j = i;
            if (c is '+' or '-') j++;
            var dotSeen = false;
            while (j < expr.Length && (char.IsDigit(expr[j]) || expr[j] == '.'))
            {
                if (expr[j] == '.')
                {
                    if (dotSeen) break;
                    dotSeen = true;
                }
                j++;
            }
            if (j > i)
            {
                var k = j;
                while (k < expr.Length && (char.IsLetter(expr[k]) || expr[k] == '%')) k++;
                tokens.Add(expr[i..k]);
                i = k;
                continue;
            }
            var k2 = i;
            while (k2 < expr.Length && !"+-*/()".Contains(expr[k2])) k2++;
            tokens.Add(expr[i..k2].Trim());
            i = k2;
        }

        var outVals = new Stack<(double Value, bool IsLength)>();
        var ops = new Stack<string>();

        foreach (var t in tokens)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            switch (t)
            {
                case "+":
                case "-":
                case "*":
                case "/":
                    while (ops.Count > 0 && Prec(ops.Peek()) >= Prec(t))
                    {
                        var op = ops.Pop();
                        if (!ApplyOp(op)) return null;
                    }
                    ops.Push(t);
                    break;
                case "(":
                    ops.Push(t);
                    break;
                case ")":
                    while (ops.Count > 0 && ops.Peek() != "(")
                    {
                        var op = ops.Pop();
                        if (!ApplyOp(op)) return null;
                    }
                    if (ops.Count == 0) return null;
                    ops.Pop();
                    break;
                default:
                    if (!TryParseOperand(t, out var operand)) return null;
                    outVals.Push(operand);
                    break;
            }
        }

        while (ops.Count > 0)
        {
            var op = ops.Pop();
            if (!ApplyOp(op)) return null;
        }

        if (outVals.Count != 1) return null;
        return outVals.Pop();

        bool TryParseOperand(string token, out (double Value, bool IsLength) operand)
        {
            operand = (0, false);
            if (string.IsNullOrWhiteSpace(token)) return false;
            var m = CalcOperandRegex().Match(token);
            if (m.Success)
            {
                var num = m.Groups["num"].Value;
                var unit = m.Groups["unit"].Value;
                if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
                if (string.IsNullOrEmpty(unit))
                {
                    operand = (v, false);
                    return true;
                }
                unit = unit.ToLowerInvariant();
                double px;
                switch (unit)
                {
                    case "px": px = v; break;
                    case "%": px = (v / 100.0) * reference; break;
                    case "em": px = v * (emReference ?? device.FontSize); break;
                    case "rem": px = v * device.FontSize; break;
                    case "pt": px = v * (96.0 / 72.0); break;
                    case "in": px = v * 96.0; break;
                    case "vh": px = (v / 100.0) * device.ViewPortHeight; break;
                    case "vw": px = (v / 100.0) * device.ViewPortWidth; break;
                    case "vmin": px = (v / 100.0) * Math.Min(device.ViewPortHeight, device.ViewPortWidth); break;
                    case "vmax": px = (v / 100.0) * Math.Max(device.ViewPortHeight, device.ViewPortWidth); break;
                    default: return false;
                }
                operand = (px, true);
                return true;
            }
            var pxv = ParseLengthToPx(token, device, reference, emReference);
            if (pxv.HasValue)
            {
                operand = (pxv.Value, true);
                return true;
            }
            return false;
        }

        bool ApplyOp(string op)
        {
            if (outVals.Count < 2) return false;
            var right = outVals.Pop();
            var left = outVals.Pop();
            switch (op)
            {
                case "+" or "-" when left.IsLength && right.IsLength:
                    outVals.Push((op == "+" ? left.Value + right.Value : left.Value - right.Value, true));
                    return true;
                case "+" or "-" when !left.IsLength && !right.IsLength:
                    outVals.Push((op == "+" ? left.Value + right.Value : left.Value - right.Value, false));
                    return true;
                case "+" or "-":
                    return false;
                case "*":
                    switch (left.IsLength)
                    {
                        case true when right.IsLength:
                            return false;
                        case false when right.IsLength:
                            outVals.Push((left.Value * right.Value, true));
                            return true;
                        default:
                            outVals.Push((left.Value * right.Value, false));
                            return true;
                    }
                case "/" when right.IsLength:
                    return false;
                case "/" when left.IsLength && !right.IsLength:
                    outVals.Push((left.Value / right.Value, true));
                    return true;
                case "/":
                    outVals.Push((left.Value / right.Value, false));
                    return true;
                default:
                    return false;
            }
        }

        static int Prec(string op) => op switch
        {
            "+" or "-" => 1,
            "*" => 2,
            "/" => 2,
            _ => 0
        };
    }

    [GeneratedRegex(@"^(?<num>[+-]?\d+(?:\.\d+)?)(?<unit>%|[a-zA-Z]+)?$", RegexOptions.Compiled)]
    private static partial Regex CalcOperandRegex();
}
