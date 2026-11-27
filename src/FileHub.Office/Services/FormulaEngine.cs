using System.Globalization;
using System.Text;
using FileHub.Office.Models;

namespace FileHub.Office.Services;

/// <summary>
/// A small Excel-compatible formula evaluator. Supports cell references, ranges, arithmetic,
/// comparison, string concatenation and the most common worksheet functions.
/// </summary>
public static class FormulaEngine
{
    private enum TokenKind { Number, Text, Reference, Range, Identifier, Operator, LeftParen, RightParen, Comma, Colon }

    private sealed record Token(TokenKind Kind, string Value);

    public static string Evaluate(string? formula, ExcelSheet sheet, IEnumerable<ExcelSheet>? sheets = null)
    {
        if (string.IsNullOrWhiteSpace(formula)) return string.Empty;
        var expression = formula.Trim();
        if (expression.StartsWith('=')) expression = expression[1..].Trim();
        if (expression.Length == 0) return string.Empty;

        try
        {
            var tokens = Tokenize(expression);
            var parser = new Parser(tokens, sheet, sheets ?? new[] { sheet }, new HashSet<string>());
            var value = parser.ParseExpression();
            return Format(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            return "#ERROR!";
        }
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '"')
            {
                var buffer = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') buffer.Append('"');
                    else buffer.Append(text[i]);
                    i++;
                }
                i++; // closing quote
                tokens.Add(new Token(TokenKind.Text, buffer.ToString()));
                continue;
            }

            if (char.IsDigit(c) || c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(new Token(TokenKind.Number, text[start..i]));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.')) i++;
                var word = text[start..i];
                // A1 / $A$1 or Sheet1!A1
                if (IsCellReference(word))
                {
                    if (i + 1 < text.Length && text[i] == ':' && IsCellReference(ReadAhead(text, i + 1)))
                    {
                        tokens.Add(new Token(TokenKind.Range, word + ":" + ReadAhead(text, i + 1)));
                        i += 1 + ReadAhead(text, i + 1).Length;
                    }
                    else tokens.Add(new Token(TokenKind.Reference, word));
                }
                else if (i < text.Length && text[i] == '!' && IsCellReference(ReadAhead(text, i + 1)))
                {
                    var reference = ReadAhead(text, i + 1);
                    i += 1 + reference.Length;
                    tokens.Add(new Token(TokenKind.Reference, word + "!" + reference));
                }
                else tokens.Add(new Token(TokenKind.Identifier, word));
                continue;
            }

            switch (c)
            {
                case '(': tokens.Add(new Token(TokenKind.LeftParen, "(")); i++; break;
                case ')': tokens.Add(new Token(TokenKind.RightParen, ")")); i++; break;
                case ',': case ';': tokens.Add(new Token(TokenKind.Comma, ",")); i++; break;
                case ':': tokens.Add(new Token(TokenKind.Colon, ":")); i++; break;
                default:
                    var op = c switch
                    {
                        '<' when i + 1 < text.Length && text[i + 1] == '=' => "<=",
                        '>' when i + 1 < text.Length && text[i + 1] == '=' => ">=",
                        '<' when i + 1 < text.Length && text[i + 1] == '>' => "<>",
                        '=' when i + 1 < text.Length && text[i + 1] == '=' => "=",
                        _ => c.ToString()
                    };
                    if (op is not ("+" or "-" or "*" or "/" or "^" or "&" or "=" or "<" or ">" or "<=" or ">=" or "<>"))
                        throw new FormatException($"Unknown operator: {op}");
                    tokens.Add(new Token(TokenKind.Operator, op));
                    i += op.Length;
                    break;
            }
        }
        return tokens;
    }

    private static string ReadAhead(string text, int index)
    {
        var end = index;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '$')) end++;
        return text[index..end];
    }

    private static bool IsCellReference(string text)
    {
        var span = text.Replace("$", string.Empty);
        if (span.Length < 2) return false;
        var i = 0;
        while (i < span.Length && char.IsLetter(span[i])) i++;
        return i > 0 && i < span.Length && int.TryParse(span.AsSpan(i), out var row) && row > 0;
    }

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly ExcelSheet _sheet;
        private readonly IReadOnlyList<ExcelSheet> _sheets;
        private readonly HashSet<string> _visiting;
        private int _position;

        public Parser(List<Token> tokens, ExcelSheet sheet, IEnumerable<ExcelSheet> sheets, HashSet<string> visiting)
        {
            _tokens = tokens;
            _sheet = sheet;
            _sheets = sheets.ToList();
            _visiting = visiting;
        }

        public object ParseExpression()
        {
            var left = ParseTerm();
            while (Current is Token(TokenKind.Operator, var op) && op is "+" or "-" or "&")
            {
                _position++;
                var right = ParseTerm();
                left = op switch
                {
                    "+" => ToNumber(left) + ToNumber(right),
                    "-" => ToNumber(left) - ToNumber(right),
                    "&" => ToText(left) + ToText(right),
                    _ => left
                };
            }
            return left;
        }

        private object ParseTerm()
        {
            var left = ParsePower();
            while (Current is Token(TokenKind.Operator, var op) && op is "*" or "/")
            {
                _position++;
                var right = ParsePower();
                var divisor = ToNumber(right);
                left = op == "*" ? ToNumber(left) * divisor
                    : divisor == 0 ? "#DIV/0!" : ToNumber(left) / divisor;
            }
            return left;
        }

        private object ParsePower()
        {
            var left = ParseComparison();
            if (Current is Token(TokenKind.Operator, "^"))
            {
                _position++;
                return Math.Pow(ToNumber(left), ToNumber(ParsePower()));
            }
            return left;
        }

        private object ParseComparison()
        {
            var left = ParseUnary();
            if (Current is Token(TokenKind.Operator, var op) && op is "=" or "<" or ">" or "<=" or ">=" or "<>")
            {
                _position++;
                var right = ParseUnary();
                var result = op switch
                {
                    "=" => ValuesEqual(left, right),
                    "<>" => !ValuesEqual(left, right),
                    "<" => Compare(left, right) < 0,
                    ">" => Compare(left, right) > 0,
                    "<=" => Compare(left, right) <= 0,
                    ">=" => Compare(left, right) >= 0,
                    _ => false
                };
                return result;
            }
            return left;
        }

        private object ParseUnary()
        {
            if (Current is Token(TokenKind.Operator, "-"))
            {
                _position++;
                return -ToNumber(ParseUnary());
            }
            if (Current is Token(TokenKind.Operator, "+"))
            {
                _position++;
                return ParseUnary();
            }
            return ParsePrimary();
        }

        private object ParsePrimary()
        {
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.Number:
                    _position++;
                    return double.TryParse(token.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
                        ? number : 0d;
                case TokenKind.Text:
                    _position++;
                    return token.Value;
                case TokenKind.Reference:
                    _position++;
                    return ReadReference(token.Value);
                case TokenKind.Range:
                    _position++;
                    return ReadRange(token.Value);
                case TokenKind.Identifier when Peek(1) is Token(TokenKind.LeftParen, _):
                    return CallFunction(token.Value);
                case TokenKind.LeftParen:
                    _position++;
                    var value = ParseExpression();
                    if (Current.Kind == TokenKind.RightParen) _position++;
                    return value;
                default:
                    throw new FormatException($"Unexpected token: {token.Value}");
            }
        }

        private object CallFunction(string name)
        {
            _position += 2; // name and '('
            var args = new List<object>();
            while (Current.Kind != TokenKind.RightParen && _position < _tokens.Count)
            {
                args.Add(ParseExpression());
                if (Current.Kind == TokenKind.Comma) _position++;
                else break;
            }
            if (Current.Kind == TokenKind.RightParen) _position++;
            var upper = name.ToUpperInvariant();

            double Sum() => args.SelectMany(Flatten).Sum(ToNumber);
            double Average()
            {
                var numbers = args.SelectMany(Flatten).Select(ToNumber).ToList();
                return numbers.Count == 0 ? 0 : numbers.Average();
            }

            return upper switch
            {
                "SUM" => Sum(),
                "AVERAGE" => Average(),
                "MIN" => MinMax(args, true),
                "MAX" => MinMax(args, false),
                "COUNT" => (double)args.SelectMany(Flatten).Count(v => v is double or int or long),
                "ABS" => Math.Abs(ToNumber(args.ElementAtOrDefault(0))),
                "ROUND" => Math.Round(ToNumber(args.ElementAtOrDefault(0)),
                    (int)ToNumber(args.ElementAtOrDefault(1))),
                "INT" => Math.Floor(ToNumber(args.ElementAtOrDefault(0))),
                "MOD" => ToNumber(args.ElementAtOrDefault(1)) == 0
                    ? 0d : ToNumber(args.ElementAtOrDefault(0)) % ToNumber(args.ElementAtOrDefault(1)),
                "POWER" => Math.Pow(ToNumber(args.ElementAtOrDefault(0)), ToNumber(args.ElementAtOrDefault(1))),
                "SQRT" => Math.Sqrt(ToNumber(args.ElementAtOrDefault(0))),
                "IF" => ToBoolean(args.ElementAtOrDefault(0))
                    ? args.ElementAtOrDefault(1) ?? string.Empty
                    : args.ElementAtOrDefault(2) ?? string.Empty,
                "LEN" => (double)ToText(args.ElementAtOrDefault(0)).Length,
                "UPPER" => ToText(args.ElementAtOrDefault(0)).ToUpperInvariant(),
                "LOWER" => ToText(args.ElementAtOrDefault(0)).ToLowerInvariant(),
                "TRIM" => ToText(args.ElementAtOrDefault(0)).Trim(),
                "CONCAT" or "CONCATENATE" => string.Concat(args.SelectMany(Flatten).Select(ToText)),
                _ => throw new ArgumentException($"Unknown function: {name}")
            };
        }

        private static IEnumerable<object> Flatten(object value)
        {
            if (value is IEnumerable<object> list)
            {
                foreach (var item in list)
                foreach (var inner in Flatten(item))
                    yield return inner;
            }
            else yield return value;
        }

        private static double MinMax(IReadOnlyList<object> args, bool min)
        {
            var numbers = args.SelectMany(Flatten).Select(ToNumber).ToList();
            if (numbers.Count == 0) return 0;
            return min ? numbers.Min() : numbers.Max();
        }

        private object ReadReference(string raw)
        {
            var parts = raw.Replace("$", string.Empty).Split('!');
            var sheet = parts.Length == 2
                ? _sheets.FirstOrDefault(s => string.Equals(s.Name, parts[0], StringComparison.OrdinalIgnoreCase)) ?? _sheet
                : _sheet;
            var reference = parts[^1];
            var (row, column) = ExcelSheet.ParseReference(reference);
            var cell = sheet.Get(row, column);
            if (cell is null) return string.Empty;
            if (!cell.HasFormula) return cell.Display;

            var key = $"{sheet.Name}!{cell.Reference}";
            if (!_visiting.Add(key)) return "#CYCLE!";
            try
            {
                var tokens = Tokenize(cell.Formula!.TrimStart('='));
                return new Parser(tokens, sheet, _sheets, _visiting).ParseExpression();
            }
            finally
            {
                _visiting.Remove(key);
            }
        }

        private IEnumerable<object> ReadRange(string raw)
        {
            var parts = raw.Split(':');
            var first = ExcelSheet.ParseReference(parts[0].Replace("$", string.Empty));
            var last = ExcelSheet.ParseReference(parts[1].Replace("$", string.Empty));
            var result = new List<object>();
            for (var row = first.Row; row <= last.Row; row++)
            for (var column = first.Column; column <= last.Column; column++)
            {
                var cell = _sheet.Get(row, column);
                if (cell is null) continue;
                if (cell.HasFormula)
                {
                    var key = $"{_sheet.Name}!{cell.Reference}";
                    if (!_visiting.Add(key)) { result.Add("#CYCLE!"); continue; }
                    try
                    {
                        result.Add(new Parser(Tokenize(cell.Formula!.TrimStart('=')), _sheet, _sheets, _visiting).ParseExpression());
                    }
                    finally { _visiting.Remove(key); }
                }
                else result.Add(cell.Display);
            }
            return result;
        }

        private Token Current => _position < _tokens.Count ? _tokens[_position] : new Token(TokenKind.Number, "0");
        private Token Peek(int offset) => _position + offset < _tokens.Count
            ? _tokens[_position + offset] : new Token(TokenKind.Number, "0");

        private static bool ToBoolean(object? value) => value switch
        {
            bool b => b,
            double d => d != 0,
            string s => s.Equals("TRUE", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        private static bool ValuesEqual(object? left, object? right)
        {
            if (left is double a && right is double b) return Math.Abs(a - b) < 1e-9;
            return string.Equals(ToText(left), ToText(right), StringComparison.OrdinalIgnoreCase);
        }

        private static int Compare(object? left, object? right)
        {
            if (left is double a && right is double b) return a.CompareTo(b);
            return string.Compare(ToText(left), ToText(right), StringComparison.OrdinalIgnoreCase);
        }

        private static double ToNumber(object? value) => value switch
        {
            double d => d,
            int i => i,
            bool b => b ? 1 : 0,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0
        };

        private static string ToText(object? value) => value switch
        {
            double d => d.ToString("0.############", CultureInfo.InvariantCulture),
            bool b => b ? "TRUE" : "FALSE",
            string s => s,
            null => string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string Format(object value) => value switch
    {
        double d => d.ToString("0.############", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => value.ToString() ?? string.Empty
    };
}
