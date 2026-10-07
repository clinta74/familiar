using System.Globalization;
using System.Text.Json.Nodes;
using Familiar.Agent.Tools;
using Familiar.Domain.Tools;

namespace Familiar.Server.Maths;

/// <summary>
/// calculate: arithmetic worked out here, not guessed - a language model's sums are often a digit
/// out. Nothing leaves the house, so it doesn't need look-ups turned on.
/// </summary>
public sealed class CalculateTool : ITool
{
    public ToolDefinition Definition { get; } = new(
        "calculate",
        "Works out arithmetic exactly. Use for any sum you'd otherwise do in your head: totals, splitting a bill, percentages, " +
        "tips, areas. Write it as an expression: + - * / ^ ( ), 15% (fifteen percent), \"15% of 80\", sqrt(2), round(x, 2), " +
        "min, max, abs, floor, ceil, ln, log, sin/cos/tan in degrees, pi. No thousands separators: 1200, not 1,200.",
        ToolSchema.Object(["expression"], ("expression", ToolSchema.String("The sum, e.g. \"(42.50 + 18) * 1.2 / 3\"."))),
        ToolRisk.Read);

    public string Describe(JsonObject arguments) => $"Work out {ToolArgs.String(arguments, "expression")}";

    public Task<ToolResult> InvokeAsync(ToolContext context, JsonObject arguments, CancellationToken ct)
    {
        var expression = ToolArgs.String(arguments, "expression");
        if (expression is null)
        {
            return Task.FromResult(ToolResult.Failure("Give the sum as an expression, e.g. \"12 * 4.5\"."));
        }

        try
        {
            var value = Calculator.Evaluate(expression);
            var shown = Calculator.Show(value);
            return Task.FromResult(ToolResult.Success(
                new JsonObject { ["ok"] = true, ["expression"] = expression, ["result"] = shown },
                $"{expression} = {shown}"));
        }
        catch (FormatException exception)
        {
            return Task.FromResult(ToolResult.Failure($"Couldn't work that out: {exception.Message} Rewrite the expression and try once more."));
        }
    }
}

/// <summary>
/// A small, safe arithmetic evaluator: numbers, operators, brackets and a fixed list of functions.
/// Nothing is ever executed - it's parsed and computed, and anything else is an error.
/// </summary>
public static class Calculator
{
    private const int MaxLength = 300;
    private const int MaxDepth = 40;

    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI, ["π"] = Math.PI, ["e"] = Math.E, ["tau"] = Math.Tau,
    };

    private static readonly Dictionary<string, (int Min, int Max, Func<double[], double> Run)> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqrt"] = (1, 1, a => Math.Sqrt(a[0])),
        ["cbrt"] = (1, 1, a => Math.Cbrt(a[0])),
        ["abs"] = (1, 1, a => Math.Abs(a[0])),
        ["floor"] = (1, 1, a => Math.Floor(a[0])),
        ["ceil"] = (1, 1, a => Math.Ceiling(a[0])),
        ["round"] = (1, 2, a => Math.Round(a[0], a.Length > 1 ? (int)Math.Clamp(a[1], 0, 15) : 0, MidpointRounding.AwayFromZero)),
        ["min"] = (1, 20, a => a.Min()),
        ["max"] = (1, 20, a => a.Max()),
        ["pow"] = (2, 2, a => Math.Pow(a[0], a[1])),
        ["ln"] = (1, 1, a => Math.Log(a[0])),
        ["log"] = (1, 2, a => a.Length > 1 ? Math.Log(a[0], a[1]) : Math.Log10(a[0])),
        ["log10"] = (1, 1, a => Math.Log10(a[0])),
        ["log2"] = (1, 1, a => Math.Log2(a[0])),
        ["exp"] = (1, 1, a => Math.Exp(a[0])),
        // Degrees: what people mean by "sine of 30".
        ["sin"] = (1, 1, a => Math.Sin(a[0] * Math.PI / 180)),
        ["cos"] = (1, 1, a => Math.Cos(a[0] * Math.PI / 180)),
        ["tan"] = (1, 1, a => Math.Tan(a[0] * Math.PI / 180)),
        ["asin"] = (1, 1, a => Math.Asin(a[0]) * 180 / Math.PI),
        ["acos"] = (1, 1, a => Math.Acos(a[0]) * 180 / Math.PI),
        ["atan"] = (1, 1, a => Math.Atan(a[0]) * 180 / Math.PI),
    };

    /// <summary>The value of [expression]; a <see cref="FormatException"/> saying what's wrong if it has none.</summary>
    public static double Evaluate(string expression)
    {
        if (expression.Length > MaxLength)
        {
            throw new FormatException("It's too long.");
        }

        var parser = new Parser(Normalise(expression));
        var value = parser.Expression(0);
        parser.End();
        return double.IsFinite(value) ? value : throw new FormatException(double.IsNaN(value) ? "It has no answer (like the square root of a negative number)." : "The answer is too big (or divides by zero).");
    }

    /// <summary>The answer as it should be said: ten significant figures, no exponent for everyday sizes.</summary>
    public static string Show(double value)
    {
        var rounded = double.Parse(value.ToString("G10", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (rounded == 0)
        {
            return "0";
        }

        return Math.Abs(rounded) is >= 1e-6 and < 1e15
            ? rounded.ToString("0.##########", CultureInfo.InvariantCulture)
            : rounded.ToString("G10", CultureInfo.InvariantCulture);
    }

    /// <summary>The ways people write the operators, as the parser's.</summary>
    private static string Normalise(string expression) => expression
        .Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace("**", "^", StringComparison.Ordinal)
        .Replace('$', ' ').Replace('£', ' ').Replace('€', ' ');

    private sealed class Parser(string text)
    {
        private int _at;

        public void End()
        {
            Skip();
            if (_at < text.Length)
            {
                throw new FormatException($"Didn't understand \"{text[_at..]}\".");
            }
        }

        /// <summary>Sums: terms joined by + and -.</summary>
        public double Expression(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("It's nested too deeply.");
            }

            var value = Term(depth);
            while (true)
            {
                if (Take('+'))
                {
                    value += Term(depth);
                }
                else if (Take('-'))
                {
                    value -= Term(depth);
                }
                else
                {
                    return value;
                }
            }
        }

        /// <summary>Products: factors joined by *, /, "mod", and "of" (as in "15% of 80").</summary>
        private double Term(int depth)
        {
            var value = Unary(depth);
            while (true)
            {
                if (Take('*') || Word("x") || Word("of") || Word("times"))
                {
                    value *= Unary(depth);
                }
                else if (Take('/'))
                {
                    value /= Unary(depth);
                }
                else if (Word("mod"))
                {
                    value %= Unary(depth);
                }
                else
                {
                    return value;
                }
            }
        }

        /// <summary>A sign, then a power: -2^2 is -4, as on paper.</summary>
        private double Unary(int depth)
        {
            if (Take('-'))
            {
                return -Unary(depth);
            }

            if (Take('+'))
            {
                return Unary(depth);
            }

            var value = Postfix(depth);
            return Take('^') ? Math.Pow(value, Unary(depth + 1)) : value;
        }

        /// <summary>A value, then any percent signs or factorials after it.</summary>
        private double Postfix(int depth)
        {
            var value = Primary(depth);
            while (true)
            {
                if (Take('%'))
                {
                    value /= 100;
                }
                else if (Take('!'))
                {
                    value = Factorial(value);
                }
                else
                {
                    return value;
                }
            }
        }

        private double Primary(int depth)
        {
            Skip();
            if (Take('('))
            {
                var inner = Expression(depth + 1);
                Expect(')');
                return inner;
            }

            if (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] == '.'))
            {
                var start = _at;
                while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] == '.'))
                {
                    _at++;
                }

                // An exponent: 1.5e6.
                if (_at + 1 < text.Length && text[_at] is 'e' or 'E' && (char.IsAsciiDigit(text[_at + 1]) || (text[_at + 1] is '-' or '+' && _at + 2 < text.Length && char.IsAsciiDigit(text[_at + 2]))))
                {
                    _at += 2;
                    while (_at < text.Length && char.IsAsciiDigit(text[_at]))
                    {
                        _at++;
                    }
                }

                return double.TryParse(text[start.._at], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? number
                    : throw new FormatException($"\"{text[start.._at]}\" isn't a number.");
            }

            if (_at < text.Length && (char.IsLetter(text[_at]) || text[_at] == 'π'))
            {
                var start = _at;
                while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == 'π'))
                {
                    _at++;
                }

                var name = text[start.._at];
                if (Functions.TryGetValue(name, out var function))
                {
                    Expect('(');
                    var arguments = new List<double> { Expression(depth + 1) };
                    while (Take(','))
                    {
                        arguments.Add(Expression(depth + 1));
                    }

                    Expect(')');
                    if (arguments.Count < function.Min || arguments.Count > function.Max)
                    {
                        throw new FormatException($"{name} doesn't take {arguments.Count} numbers.");
                    }

                    return function.Run([.. arguments]);
                }

                if (Constants.TryGetValue(name, out var constant))
                {
                    return constant;
                }

                throw new FormatException($"\"{name}\" isn't something it knows.");
            }

            throw new FormatException(_at < text.Length ? $"Didn't expect \"{text[_at]}\"." : "It stops before the end.");
        }

        private static double Factorial(double n)
        {
            if (n < 0 || n != Math.Floor(n) || n > 170)
            {
                throw new FormatException("Factorials are for whole numbers from 0 to 170.");
            }

            var result = 1.0;
            for (var i = 2; i <= n; i++)
            {
                result *= i;
            }

            return result;
        }

        private void Skip()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
            {
                _at++;
            }
        }

        private bool Take(char c)
        {
            Skip();
            if (_at < text.Length && text[_at] == c)
            {
                _at++;
                return true;
            }

            return false;
        }

        /// <summary>A whole word, like "of" - not the start of a longer one.</summary>
        private bool Word(string word)
        {
            Skip();
            if (string.Compare(text, _at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (_at + word.Length == text.Length || !char.IsLetter(text[_at + word.Length])))
            {
                _at += word.Length;
                return true;
            }

            return false;
        }

        private void Expect(char c)
        {
            if (!Take(c))
            {
                throw new FormatException($"Expected \"{c}\".");
            }
        }
    }
}
