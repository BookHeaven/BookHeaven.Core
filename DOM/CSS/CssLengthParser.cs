using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AngleSharp.Css;
using AngleSharp.Dom;

namespace BookHeaven.Core.DOM.CSS;

public static partial class CssLengthParser
{
    // CSS custom properties per document: style tags don't change once a
    // document is parsed, so the extracted map is cached per document instance
    // instead of re-scanning every <style> on each var() lookup.
    private static readonly ConditionalWeakTable<IDocument, Dictionary<string, string>> CssVariablesCache = new();

    // Parsed lengths per document: the same literal ("1.2em", "14pt", "0", ...) is
    // parsed once per element per property, so with a per-document memo each distinct
    // (text, reference, emReference) triple is parsed once. A document is always
    // produced by a single parser/render device, so the device never mixes within a
    // memo. `el` is forwarded by callers but never influences the result.
    private static readonly ConditionalWeakTable<IDocument, Dictionary<(string, float, float), float?>> LengthCache = new();

    public static float? ParseLengthToPx(string? s, IRenderDevice? renderDevice, float reference, float? emReference = null, IDocument? doc = null, IElement? el = null)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cur = s.Trim();

        if (doc != null)
        {
            var memo = LengthCache.GetOrCreateValue(doc);
            var key = (cur, reference, emReference ?? float.NegativeInfinity);
            if (memo.TryGetValue(key, out var cached)) return cached;
            var result = ParseLengthToPxCore(cur, renderDevice, reference, emReference, doc, el);
            memo[key] = result;
            return result;
        }

        return ParseLengthToPxCore(cur, renderDevice, reference, emReference, doc, el);
    }

    private static float? ParseLengthToPxCore(string cur, IRenderDevice? renderDevice, float reference, float? emReference = null, IDocument? doc = null, IElement? el = null)
    {

        if (string.Equals(cur, "auto", StringComparison.OrdinalIgnoreCase) || string.Equals(cur, "none", StringComparison.OrdinalIgnoreCase)) return null;

        if (doc != null && cur.Contains("var(", StringComparison.Ordinal))
        {
            try
            {
                var vars = ExtractCssVariables(doc);
                cur = ResolveCssVarsInString(cur, vars);
            }
            catch { }
        }

        // Support CSS functions max(), min(), clamp() by evaluating their comma-separated arguments
        if (cur.Contains("max(", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var inner = ExtractFunctionInner(cur, "max");
                if (!string.IsNullOrWhiteSpace(inner))
                {
                    var parts = SplitTopLevelCommas(inner);
                    float? best = null;
                    foreach (var p in parts)
                    {
                        var v = ParseLengthToPx(p, renderDevice, reference, emReference, doc, el);
                        if (v.HasValue) best = best.HasValue ? Math.Max(best.Value, v.Value) : v.Value;
                    }
                    return best;
                }
            }
            catch { }
        }

        if (cur.Contains("min(", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var inner = ExtractFunctionInner(cur, "min");
                if (!string.IsNullOrWhiteSpace(inner))
                {
                    var parts = SplitTopLevelCommas(inner);
                    float? best = null;
                    foreach (var p in parts)
                    {
                        var v = ParseLengthToPx(p, renderDevice, reference, emReference, doc, el);
                        if (v.HasValue) best = best.HasValue ? Math.Min(best.Value, v.Value) : v.Value;
                    }
                    return best;
                }
            }
            catch { }
        }

        if (cur.Contains("clamp(", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var inner = ExtractFunctionInner(cur, "clamp");
                if (!string.IsNullOrWhiteSpace(inner))
                {
                    var parts = SplitTopLevelCommas(inner);
                    if (parts.Count == 3)
                    {
                        var minv = ParseLengthToPx(parts[0], renderDevice, reference, emReference, doc, el);
                        var valv = ParseLengthToPx(parts[1], renderDevice, reference, emReference, doc, el);
                        var maxv = ParseLengthToPx(parts[2], renderDevice, reference, emReference, doc, el);
                        if (valv.HasValue)
                        {
                            double v = valv.Value;
                            if (minv.HasValue) v = Math.Max(v, minv.Value);
                            if (maxv.HasValue) v = Math.Min(v, maxv.Value);
                            return (float)v;
                        }
                    }
                }
            }
            catch { }
        }

        if (cur.Contains("calc(", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var inner = ExtractCalcInner(cur);
                if (!string.IsNullOrWhiteSpace(inner))
                {
                    var evaluated = EvaluateCalcExpression(inner, renderDevice, reference, emReference, doc, el);
                    if (evaluated.HasValue) return evaluated.Value;
                }
            }
            catch { }
        }

        if (cur.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
        {
            var num = cur[..^3].Trim();
            if (float.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                return v * (float)(renderDevice?.FontSize ?? 16f);
            }
            return null;
        }

        if (cur.EndsWith("em", StringComparison.OrdinalIgnoreCase))
        {
            var num = cur[..^2].Trim();
            if (float.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                var baseEm = emReference ?? (float)(renderDevice?.FontSize ?? 16f);
                return v * baseEm;
            }
            return null;
        }

        if (cur.EndsWith('%'))
        {
            var num = cur[..^1].Trim();
            if (float.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                return (pct / 100f) * reference;
            }
            return null;
        }

        if (cur.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            var num = cur.Substring(0, cur.Length - 2).Trim();
            if (float.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            return null;
        }

        // Print units, common in ebook CSS: 1in = 96px, 1pt = 1/72in. The digit/dot
        // guard keeps tokens like "9min" from matching the "in" suffix.
        if (cur.Length > 2 && (char.IsDigit(cur[^3]) || cur[^3] == '.') &&
            (cur.EndsWith("pt", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("in", StringComparison.OrdinalIgnoreCase)))
        {
            var unit = cur[^2..].ToLowerInvariant();
            var num = cur.Substring(0, cur.Length - 2).Trim();
            if (float.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                return unit == "pt" ? v * (96f / 72f) : v * 96f;
            return null;
        }

        if (cur.EndsWith("vh", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("vw", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("vmin", StringComparison.OrdinalIgnoreCase) || cur.EndsWith("vmax", StringComparison.OrdinalIgnoreCase))
        {
            var unit = cur.EndsWith("vmin", StringComparison.OrdinalIgnoreCase) ? "vmin" : cur.EndsWith("vmax", StringComparison.OrdinalIgnoreCase) ? "vmax" : cur[^2..].ToLowerInvariant();
            string numStr = cur.Substring(0, cur.Length - unit.Length).Trim();
            if (float.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                float val = 0f;
                if (unit == "vh") val = (v / 100f) * (renderDevice?.ViewPortHeight ?? 0);
                else if (unit == "vw") val = (v / 100f) * (renderDevice?.ViewPortWidth ?? 0);
                else if (unit == "vmin") val = (v / 100f) * Math.Min((renderDevice?.ViewPortWidth ?? 0), (renderDevice?.ViewPortHeight ?? 0));
                else if (unit == "vmax") val = (v / 100f) * Math.Max((renderDevice?.ViewPortWidth ?? 0), (renderDevice?.ViewPortHeight ?? 0));
                return val;
            }
            return null;
        }

        // Special-case: zero without unit is valid as 0px in CSS (e.g. "margin: 0").
        // For other bare numeric values (e.g. "1.5"), treat them as unitless
        // so callers (like line-height handling) can interpret them as multipliers.
        if (float.TryParse(cur, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var raw))
        {
            if (raw == 0f) return 0f;
        }
        return null;
    }

    private static string ExtractCalcInner(string s)
    {
        var idx = s.IndexOf("calc(", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;
        int start = s.IndexOf('(', idx) + 1;
        int depth = 1;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return s.Substring(start, i - start).Trim();
            }
        }
        return string.Empty;
    }

    private static string ExtractFunctionInner(string s, string functionName)
    {
        var idx = s.IndexOf(functionName + "(", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;
        int start = s.IndexOf('(', idx) + 1;
        int depth = 1;
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return s.Substring(start, i - start).Trim();
            }
        }
        return string.Empty;
    }

    private static List<string> SplitTopLevelCommas(string s) => SplitTopLevel(s, c => c == ',');

    /// <summary>
    /// Splits <paramref name="s"/> on the characters accepted by
    /// <paramref name="isSeparator"/> at parenthesis depth 0 (separators inside
    /// parentheses never split).
    /// </summary>
    internal static List<string> SplitTopLevel(string s, Func<char, bool> isSeparator)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return parts;
        int depth = 0; int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (depth == 0 && isSeparator(s[i]))
            {
                parts.Add(s.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start).Trim());
        return parts;
    }

    private static Dictionary<string, string> ExtractCssVariables(IDocument doc)
    {
        return CssVariablesCache.GetOrAdd(doc, static doc =>
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var styles = doc.QuerySelectorAll("style");
                foreach (var s in styles)
                {
                    var text = s.TextContent;
                    foreach (Match m in VarDefinitionRegex().Matches(text))
                    {
                        var name = m.Groups["name"].Value.Trim();
                        var value = m.Groups["value"].Value.Trim();
                        dict.TryAdd(name, value);
                    }
                }
            }
            catch { }
            return dict;
        });
    }

    private static string ResolveCssVarsInString(string input, Dictionary<string, string> vars)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        var safety = 0;
        var cur = input;
        while (safety++ < 10)
        {
            var m = VarUsageRegex().Match(cur);
            if (!m.Success) break;
            var name = m.Groups["name"].Value.Trim();
            var fallbackGroup = m.Groups["fallback"];
            var replacement = string.Empty;
            if (vars.TryGetValue(name, out var val)) replacement = val;
            else if (fallbackGroup.Success) replacement = fallbackGroup.Value.Trim();
            cur = string.Concat(cur.AsSpan(0, m.Index), replacement, cur.AsSpan(m.Index + m.Length));
        }
        return cur;
    }

    private static float? EvaluateCalcExpression(string expr, IRenderDevice? renderDevice, float reference, float? emReference, IDocument? doc, IElement? el)
    {
        if (string.IsNullOrWhiteSpace(expr)) return null;
        var tokens = new List<string>();
        for (var i = 0; i < expr.Length;)
        {
            if (char.IsWhiteSpace(expr[i])) { i++; continue; }
            var c = expr[i];
            if (c is '+' or '-' or '*' or '/' or '(' or ')')
            {
                tokens.Add(c.ToString()); i++; continue;
            }
            var j = i;
            if (c is '+' or '-') j++;
            var dotSeen = false;
            while (j < expr.Length && (char.IsDigit(expr[j]) || expr[j] == '.')) { if (expr[j] == '.') { if (dotSeen) break; dotSeen = true; } j++; }
            if (j > i)
            {
                var k = j;
                while (k < expr.Length && (char.IsLetter(expr[k]) || expr[k] == '%')) k++;
                tokens.Add(expr.Substring(i, k - i));
                i = k; continue;
            }
            var k2 = i;
            while (k2 < expr.Length && !"+-*/()".Contains(expr[k2])) k2++;
            tokens.Add(expr.Substring(i, k2 - i).Trim());
            i = k2;
        }

        var outVals = new Stack<(double value, bool isLength)>();
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
                {
                    while (ops.Count > 0 && Prec(ops.Peek()) >= Prec(t))
                    {
                        var op = ops.Pop();
                        if (!ApplyOp(op)) return null;
                    }
                    ops.Push(t);
                    break;
                }
                case "(":
                    ops.Push(t);
                    break;
                case ")":
                {
                    while (ops.Count > 0 && ops.Peek() != "(")
                    {
                        var op = ops.Pop(); if (!ApplyOp(op)) return null;
                    }
                    if (ops.Count == 0) return null;
                    ops.Pop();
                    break;
                }
                default:
                {
                    if (!TryParseOperand(t, out var operand)) return null;
                    outVals.Push(operand);
                    break;
                }
            }
        }

        while (ops.Count > 0)
        {
            var op = ops.Pop(); if (!ApplyOp(op)) return null;
        }

        if (outVals.Count != 1) return null;
        var final = outVals.Pop();
        return (float)final.value;

        bool TryParseOperand(string token, out (double value, bool isLength) operand)
        {
            operand = (0, false);
            if (string.IsNullOrWhiteSpace(token)) return false;
            var m = CalcOperandRegex().Match(token);
            if (m.Success)
            {
                var num = m.Groups["num"].Value;
                var unit = m.Groups["unit"].Value;
                if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
                if (string.IsNullOrEmpty(unit)) { operand = (v, false); return true; }
                unit = unit.ToLowerInvariant();
                double px;
                switch (unit)
                {
                    case "px": px = v; break;
                    case "%": px = (v / 100.0) * reference; break;
                    case "em": px = v * (emReference ?? renderDevice?.FontSize ?? 16f); break;
                    case "rem": px = v * (renderDevice?.FontSize ?? 16f); break;
                    case "pt": px = v * (96.0 / 72.0); break;
                    case "vh": px = (v / 100.0) * (renderDevice?.ViewPortHeight ?? 0); break;
                    case "vw": px = (v / 100.0) * (renderDevice?.ViewPortWidth ?? 0); break;
                    case "vmin": px = (v / 100.0) * Math.Min((renderDevice?.ViewPortHeight ?? 0), (renderDevice?.ViewPortWidth ?? 0)); break;
                    case "vmax": px = (v / 100.0) * Math.Max((renderDevice?.ViewPortWidth ?? 0), (renderDevice?.ViewPortHeight ?? 0)); break;
                    default: return false;
                }
                operand = (px, true); return true;
            }
            try
            {
                var pxv = ParseLengthToPx(token, renderDevice, reference, emReference, doc, el);
                if (pxv.HasValue) { operand = (pxv.Value, true); return true; }
            }
            catch { }
            return false;
        }

        bool ApplyOp(string op)
        {
            if (outVals.Count < 2) return false;
            var right = outVals.Pop();
            var left = outVals.Pop();
            switch (op)
            {
                case "+" or "-" when left.isLength && right.isLength:
                {
                    var res = op == "+" ? left.value + right.value : left.value - right.value;
                    outVals.Push((res, true));
                    return true;
                }
                case "+" or "-" when !left.isLength && !right.isLength:
                {
                    var res = op == "+" ? left.value + right.value : left.value - right.value;
                    outVals.Push((res, false));
                    return true;
                }
                case "+" or "-":
                    return false;
                case "*":
                    switch (left.isLength)
                    {
                        case true when right.isLength:
                            return false;
                        case false when right.isLength:
                            outVals.Push((left.value * right.value, true)); return true;
                        default:
                            outVals.Push((left.value * right.value, false)); return true;
                    }
                case "/" when right.isLength:
                    return false;
                case "/" when left.isLength && !right.isLength:
                    outVals.Push((left.value / right.value, true)); return true;
                case "/":
                    outVals.Push((left.value / right.value, false)); return true;
                default:
                    return false;
            }
        }

        int Prec(string op) => op switch
        {
            "+" or "-" => 1,
            "*" => 2,
            "/" => 2,
            _ => 0
        };
    }

    [GeneratedRegex(@"--(?<name>[\w-]+)\s*:\s*(?<value>[^;]+);", RegexOptions.Compiled)]
    private static partial Regex VarDefinitionRegex();
    [GeneratedRegex(@"var\(\s*--(?<name>[\w-]+)(?:\s*,\s*(?<fallback>[^)]+))?\s*\)", RegexOptions.Compiled)]
    private static partial Regex VarUsageRegex();
    [GeneratedRegex(@"^(?<num>[+-]?\d+(?:\.\d+)?)(?<unit>%|[a-zA-Z]+)?$", RegexOptions.Compiled)]
    private static partial Regex CalcOperandRegex();
}
