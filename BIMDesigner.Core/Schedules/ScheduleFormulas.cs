using System.Globalization;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Schedules;

/// <summary>What a calculated field holds, and so how it is written.</summary>
public enum CalculatedKind
{
    Number,
    Length,
    Area,
    Volume,
    Currency,

    /// <summary>Yes where the formula comes to anything but nought: a test.</summary>
    YesNo,

    /// <summary>What a field is of its total - the whole schedule's, or its group's - in per cent.</summary>
    Percentage
}

/// <summary>
/// A calculated value in a schedule (specification section 6.4): a formula over the row's other
/// fields - "Width * Height", "if(Width < 800, 1, 0)" - or a field as a percentage of its total.
/// Lengths are millimetres, areas square millimetres, volumes cubic millimetres, as the model
/// holds them.
/// </summary>
public sealed class CalculatedField
{
    public string Name { get; set; } = "Calculated";

    public CalculatedKind Kind { get; set; } = CalculatedKind.Number;

    public string Formula { get; set; } = string.Empty;

    /// <summary>For a percentage: the field it is a share of.</summary>
    public string PercentageOf { get; set; } = string.Empty;

    /// <summary>For a percentage: of its group's total rather than the whole schedule's.</summary>
    public bool ByGroup { get; set; }

    public CalculatedField Copy() => new() { Name = Name, Kind = Kind, Formula = Formula, PercentageOf = PercentageOf, ByGroup = ByGroup };
}

/// <summary>How a conditional format tests a cell.</summary>
public enum FormatTest
{
    EqualTo,
    NotEqualTo,
    GreaterThan,
    GreaterThanOrEqualTo,
    LessThan,
    LessThanOrEqualTo,
    Between,
    NotBetween,
    Contains,
    DoesNotContain,
    HasValue,
    HasNoValue
}

/// <summary>
/// Conditional formatting (specification section 6.4): a field's cells coloured where they pass
/// a test - "Clear Width less than 775", "Area not between 7 and 12 m²" - so what needs looking at
/// stands out. Numbers are compared as the model holds them; text alphabetically.
/// </summary>
public sealed class ConditionalFormat
{
    public string Field { get; set; } = string.Empty;

    public FormatTest Test { get; set; } = FormatTest.LessThan;

    public string Value { get; set; } = string.Empty;

    /// <summary>The other end of a range, for Between and Not Between.</summary>
    public string Value2 { get; set; } = string.Empty;

    /// <summary>The cell's background where it passes.</summary>
    public ColourRgb Background { get; set; } = new(0xF8, 0xC8, 0xC8);

    public ConditionalFormat Copy() => new() { Field = Field, Test = Test, Value = Value, Value2 = Value2, Background = Background };

    /// <summary>Whether a cell of a kind - its number, if it has one, and its text - passes.</summary>
    public bool Passes(ParameterDataType type, double? number, string text)
    {
        int Compare(string against) => number is { } n && Threshold(type, against) is { } v
            ? n.CompareTo(v)
            : string.Compare(text, against, StringComparison.OrdinalIgnoreCase);

        return Test switch
        {
            FormatTest.EqualTo => Compare(Value) == 0,
            FormatTest.NotEqualTo => Compare(Value) != 0,
            FormatTest.GreaterThan => Compare(Value) > 0,
            FormatTest.GreaterThanOrEqualTo => Compare(Value) >= 0,
            FormatTest.LessThan => Compare(Value) < 0,
            FormatTest.LessThanOrEqualTo => Compare(Value) <= 0,
            FormatTest.Between => Compare(Value) >= 0 && Compare(Value2) <= 0,
            FormatTest.NotBetween => Compare(Value) < 0 || Compare(Value2) > 0,
            FormatTest.Contains => text.Contains(Value, StringComparison.OrdinalIgnoreCase),
            FormatTest.DoesNotContain => !text.Contains(Value, StringComparison.OrdinalIgnoreCase),
            FormatTest.HasValue => !string.IsNullOrWhiteSpace(text) && text != ParameterFormatter.NoValue,
            FormatTest.HasNoValue => string.IsNullOrWhiteSpace(text) || text == ParameterFormatter.NoValue,
            _ => false
        };
    }

    /// <summary>
    /// A value typed against a column, as the model holds it: "775" or "0.775 m" for a length;
    /// "7" or "7 m²" for an area and "2" or "2 m³" for a volume - read in the square and cubic
    /// metres a schedule shows them in; "25" or "25%" for a percentage. Null where it is not a number.
    /// </summary>
    public static double? Threshold(ParameterDataType type, string value)
    {
        value = (value ?? string.Empty).Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare))
            return type switch
            {
                ParameterDataType.Area => bare * Units.SquareMillimetresPerSquareMetre,
                ParameterDataType.Volume => bare * Units.CubicMillimetresPerCubicMetre,
                _ => bare
            };

        return ParameterFormatter.TryParse(type, value, out var parsed) && parsed is not null
            ? parsed switch { double d => d, int i => i, decimal m => (double)m, _ => null }
            : null;
    }
}

/// <summary>A formula that cannot be read or worked out, with why.</summary>
public sealed class FormulaException : Exception
{
    public FormulaException(string message) : base(message)
    {
    }
}

/// <summary>
/// Schedule formulas: numbers, the row's fields by name - as they are, spaces and all, or in
/// square brackets - + - * / ^, comparisons = &lt;&gt; &lt; &gt; &lt;= &gt;=, and if, and, or,
/// not, round, roundup, rounddown, abs, min, max, sqrt. Tests come to 1 or 0.
/// </summary>
public static class ScheduleFormula
{
    private static readonly string[] Functions = { "if", "and", "or", "not", "rounddown", "roundup", "round", "abs", "min", "max", "sqrt" };

    /// <summary>Works a formula out, its fields read through a lookup; the reason in a <see cref="FormulaException"/> where it cannot be.</summary>
    public static double Evaluate(string formula, IReadOnlyCollection<string> fields, Func<string, double?> value)
    {
        var parser = new Parser(formula, fields, value);
        var result = parser.Expression();
        parser.End();
        return result;
    }

    /// <summary>Why a formula cannot be read, with the fields it may use - or null where it can.</summary>
    public static string? Check(string formula, IReadOnlyCollection<string> fields)
    {
        try
        {
            Evaluate(formula, fields, _ => 1);
            return null;
        }
        catch (FormulaException problem)
        {
            return problem.Message;
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly IReadOnlyList<string> _fields;
        private readonly Func<string, double?> _value;
        private int _at;

        public Parser(string text, IReadOnlyCollection<string> fields, Func<string, double?> value)
        {
            (_text, _value) = (text ?? string.Empty, value);

            // The longest names first, so "Clear Width" is read before "Width".
            _fields = fields.OrderByDescending(field => field.Length).ToList();
        }

        public void End()
        {
            Skip();
            if (_at < _text.Length) throw new FormulaException($"Unexpected '{_text[_at]}' at {_at + 1}.");
        }

        public double Expression()
        {
            var left = Sum();
            Skip();
            foreach (var op in new[] { "<=", ">=", "<>", "=", "<", ">" })
            {
                if (!Take(op)) continue;
                var right = Sum();
                return op switch
                {
                    "<=" => left <= right ? 1 : 0,
                    ">=" => left >= right ? 1 : 0,
                    "<>" => Math.Abs(left - right) > 1e-9 ? 1 : 0,
                    "=" => Math.Abs(left - right) <= 1e-9 ? 1 : 0,
                    "<" => left < right ? 1 : 0,
                    _ => left > right ? 1 : 0
                };
            }

            return left;
        }

        private double Sum()
        {
            var value = Term();
            while (true)
            {
                if (Take("+")) value += Term();
                else if (Take("-")) value -= Term();
                else return value;
            }
        }

        private double Term()
        {
            var value = Power();
            while (true)
            {
                if (Take("*")) value *= Power();
                else if (Take("/"))
                {
                    var divisor = Power();
                    if (Math.Abs(divisor) < 1e-12) throw new FormulaException("Divided by nought.");
                    value /= divisor;
                }
                else return value;
            }
        }

        private double Power()
        {
            var value = Unary();
            return Take("^") ? Math.Pow(value, Unary()) : value;
        }

        private double Unary() => Take("-") ? -Unary() : Take("+") ? Unary() : Primary();

        private double Primary()
        {
            Skip();
            if (_at >= _text.Length) throw new FormulaException("The formula ends where a value was wanted.");

            if (Take("("))
            {
                var inner = Expression();
                if (!Take(")")) throw new FormulaException("A bracket is not closed.");
                return inner;
            }

            // A number.
            var start = _at;
            while (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '.')) _at++;
            if (_at > start)
            {
                if (!double.TryParse(_text[start.._at], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new FormulaException($"'{_text[start.._at]}' is not a number.");
                return number;
            }

            // A field in square brackets.
            if (Take("["))
            {
                var close = _text.IndexOf(']', _at);
                if (close < 0) throw new FormulaException("A square bracket is not closed.");
                var name = _text[_at..close].Trim();
                _at = close + 1;
                return Field(_fields.FirstOrDefault(field => field.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name);
            }

            // A function, then a field by its name as it is.
            foreach (var function in Functions)
            {
                if (!_text.AsSpan(_at).StartsWith(function, StringComparison.OrdinalIgnoreCase)) continue;
                var after = _at + function.Length;
                while (after < _text.Length && char.IsWhiteSpace(_text[after])) after++;
                if (after >= _text.Length || _text[after] != '(') continue;
                _at = after + 1;
                return Call(function, Arguments());
            }

            foreach (var field in _fields)
            {
                if (!_text.AsSpan(_at).StartsWith(field, StringComparison.OrdinalIgnoreCase)) continue;
                var next = _at + field.Length;
                if (next < _text.Length && (char.IsLetterOrDigit(_text[next]) || _text[next] == '_')) continue;
                _at = next;
                return Field(field);
            }

            var end = _at;
            while (end < _text.Length && (char.IsLetterOrDigit(_text[end]) || _text[end] == '_')) end++;
            throw new FormulaException($"'{(end > _at ? _text[_at..end] : _text[_at].ToString())}' is not a field of this schedule.");
        }

        private double Field(string name)
        {
            if (!_fields.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new FormulaException($"'{name}' is not a field of this schedule.");
            return _value(name) ?? throw new FormulaException($"{name} has no number here.");
        }

        private List<double> Arguments()
        {
            var arguments = new List<double>();
            if (Take(")")) return arguments;
            do arguments.Add(Expression());
            while (Take(","));
            if (!Take(")")) throw new FormulaException("A function's bracket is not closed.");
            return arguments;
        }

        private static double Call(string function, List<double> a)
        {
            void Need(int count)
            {
                if (a.Count != count) throw new FormulaException($"{function} takes {count} value{(count == 1 ? "" : "s")}.");
            }

            switch (function)
            {
                case "if": Need(3); return a[0] != 0 ? a[1] : a[2];
                case "and": return a.Count > 0 && a.All(x => x != 0) ? 1 : 0;
                case "or": return a.Any(x => x != 0) ? 1 : 0;
                case "not": Need(1); return a[0] == 0 ? 1 : 0;
                case "round": Need(1); return Math.Round(a[0], MidpointRounding.AwayFromZero);
                case "roundup": Need(1); return Math.Ceiling(a[0]);
                case "rounddown": Need(1); return Math.Floor(a[0]);
                case "abs": Need(1); return Math.Abs(a[0]);
                case "sqrt":
                    Need(1);
                    if (a[0] < 0) throw new FormulaException("The square root of less than nought.");
                    return Math.Sqrt(a[0]);
                case "min":
                    if (a.Count == 0) throw new FormulaException("min takes values.");
                    return a.Min();
                default:
                    if (a.Count == 0) throw new FormulaException("max takes values.");
                    return a.Max();
            }
        }

        private void Skip()
        {
            while (_at < _text.Length && char.IsWhiteSpace(_text[_at])) _at++;
        }

        private bool Take(string token)
        {
            Skip();
            if (!_text.AsSpan(_at).StartsWith(token, StringComparison.Ordinal)) return false;

            // "<" is not "<=" or "<>".
            if (token is "<" or ">" && _at + 1 < _text.Length && _text[_at + 1] is '=' or '>') return false;
            _at += token.Length;
            return true;
        }
    }
}
