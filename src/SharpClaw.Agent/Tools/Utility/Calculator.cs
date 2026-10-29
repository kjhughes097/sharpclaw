using System.Globalization;

namespace SharpClaw.Agent.Tools.Utility;

/// <summary>An expression the calculator refuses; <see cref="Code"/> is the tool error code.</summary>
public sealed class CalculatorException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Evaluates arithmetic over a fixed grammar (agent tools spec section 5.2). Hand-written
/// recursive descent: no identifiers beyond the listed functions, no variables, no scripting or
/// expression library, nothing that can reach code, files or the network.
/// <code>
/// expr    := term (('+' | '-') term)*
/// term    := unary (('*' | '/' | '%') unary)*
/// unary   := '-' unary | power
/// power   := primary ('^' unary)?           (right-associative; -2^2 = -4)
/// primary := number | '(' expr ')' | func '(' expr (',' expr)* ')'
/// func    := abs | round | floor | ceil | sqrt | min | max
/// </code>
/// </summary>
public sealed class Calculator
{
    public const int MaxLength = 200;
    public const int MaxDepth = 32;

    public const string InvalidExpression = "invalid_expression";
    public const string MathError = "math_error";

    private readonly string _text;
    private int _pos;
    private int _depth;

    private Calculator(string text) => _text = text;

    public static double Evaluate(string expression)
    {
        if (expression.Length > MaxLength)
            throw new CalculatorException(InvalidExpression, $"The expression is longer than {MaxLength} characters.");
        var calculator = new Calculator(expression);
        var value = calculator.Expr();
        calculator.SkipWhitespace();
        if (calculator._pos < expression.Length)
            throw calculator.Unexpected();
        return value;
    }

    private double Expr()
    {
        var value = Term();
        while (true)
        {
            if (Accept('+'))
                value = Checked(value + Term());
            else if (Accept('-'))
                value = Checked(value - Term());
            else
                return value;
        }
    }

    private double Term()
    {
        var value = Unary();
        while (true)
        {
            if (Accept('*'))
            {
                value = Checked(value * Unary());
            }
            else if (Accept('/'))
            {
                var divisor = Unary();
                value = divisor == 0 ? throw DivisionByZero() : Checked(value / divisor);
            }
            else if (Accept('%'))
            {
                var divisor = Unary();
                value = divisor == 0 ? throw DivisionByZero() : Checked(value % divisor);
            }
            else
            {
                return value;
            }
        }
    }

    private double Unary() => Accept('-') ? Checked(-Unary()) : Power();

    private double Power()
    {
        var value = Primary();
        return Accept('^') ? Checked(Math.Pow(value, Unary())) : value;
    }

    private double Primary()
    {
        SkipWhitespace();
        if (_pos >= _text.Length)
            throw new CalculatorException(InvalidExpression, "The expression ended unexpectedly.");

        var c = _text[_pos];
        if (char.IsAsciiDigit(c) || c == '.')
            return Number();
        if (c == '(')
        {
            _pos++;
            Enter();
            var value = Expr();
            Expect(')');
            _depth--;
            return value;
        }
        if (char.IsAsciiLetter(c))
            return Function();
        throw Unexpected();
    }

    private double Number()
    {
        var start = _pos;
        while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
            _pos++;
        if (_pos < _text.Length && _text[_pos] == '.')
        {
            _pos++;
            while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
                _pos++;
        }
        var token = _text[start.._pos];
        if (token == "." || !double.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            throw new CalculatorException(InvalidExpression, $"Invalid number at position {start + 1}.");
        return Checked(value);
    }

    private double Function()
    {
        var start = _pos;
        while (_pos < _text.Length && char.IsAsciiLetter(_text[_pos]))
            _pos++;
        var name = _text[start.._pos].ToLowerInvariant();
        if (name is not ("abs" or "round" or "floor" or "ceil" or "sqrt" or "min" or "max"))
            throw new CalculatorException(InvalidExpression,
                $"Unknown name at position {start + 1}; only abs, round, floor, ceil, sqrt, min and max are available, and no variables.");

        Expect('(');
        Enter();
        var args = new List<double> { Expr() };
        while (Accept(','))
            args.Add(Expr());
        Expect(')');
        _depth--;

        return name switch
        {
            "abs" => Math.Abs(Single(name, args)),
            "floor" => Math.Floor(Single(name, args)),
            "ceil" => Math.Ceiling(Single(name, args)),
            "sqrt" => Single(name, args) < 0
                ? throw new CalculatorException(MathError, "sqrt of a negative number.")
                : Math.Sqrt(args[0]),
            "round" => Round(args),
            "min" => args.Min(),
            _ => args.Max(),
        };
    }

    private static double Single(string name, List<double> args) =>
        args.Count == 1 ? args[0] : throw new CalculatorException(InvalidExpression, $"{name} takes one argument.");

    /// <summary>round(x) or round(x, digits) with 0-15 digits; halves round away from zero.</summary>
    private static double Round(List<double> args)
    {
        if (args.Count == 1)
            return Math.Round(args[0], MidpointRounding.AwayFromZero);
        if (args.Count == 2 && args[1] is >= 0 and <= 15 && args[1] == Math.Floor(args[1]))
            return Math.Round(args[0], (int)args[1], MidpointRounding.AwayFromZero);
        throw new CalculatorException(InvalidExpression, "round takes a value and optionally a whole number of digits (0-15).");
    }

    private void Enter()
    {
        if (++_depth > MaxDepth)
            throw new CalculatorException(InvalidExpression, $"The expression is nested more than {MaxDepth} levels deep.");
    }

    private bool Accept(char c)
    {
        SkipWhitespace();
        if (_pos < _text.Length && _text[_pos] == c)
        {
            _pos++;
            return true;
        }
        return false;
    }

    private void Expect(char c)
    {
        if (!Accept(c))
            throw _pos < _text.Length ? Unexpected() : new CalculatorException(InvalidExpression, $"Expected '{c}' at the end.");
    }

    private void SkipWhitespace()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            _pos++;
    }

    private CalculatorException Unexpected() =>
        new(InvalidExpression, $"Unexpected character at position {_pos + 1}.");

    private static CalculatorException DivisionByZero() => new(MathError, "Division by zero.");

    private static double Checked(double value) =>
        double.IsFinite(value) ? value : throw new CalculatorException(MathError, "The result is not a finite number.");
}
