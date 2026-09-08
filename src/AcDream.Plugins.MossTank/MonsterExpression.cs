using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AcDream.Plugins.MossTank;

internal enum MonsterValueKind
{
    Number,
    Text,
    Boolean,
}

internal readonly record struct MonsterValue
{
    private MonsterValue(
        MonsterValueKind kind,
        double number,
        string? text,
        bool boolean)
    {
        Kind = kind;
        Number = number;
        Text = text ?? string.Empty;
        Boolean = boolean;
    }

    public MonsterValueKind Kind { get; }
    public double Number { get; }
    public string Text { get; }
    public bool Boolean { get; }

    public static MonsterValue FromNumber(double value) =>
        new(MonsterValueKind.Number, value, null, false);

    public static MonsterValue FromText(string value) =>
        new(MonsterValueKind.Text, 0d, value, false);

    public static MonsterValue FromBoolean(bool value) =>
        new(MonsterValueKind.Boolean, 0d, null, value);

    public override string ToString() => Kind switch
    {
        MonsterValueKind.Number => Number.ToString(CultureInfo.InvariantCulture),
        MonsterValueKind.Text => Text,
        MonsterValueKind.Boolean => Boolean ? "true" : "false",
        _ => string.Empty,
    };
}

/// <summary>Live values exposed by VTank's <c>/vt listmonstervariables</c>.</summary>
internal readonly record struct MonsterExpressionContext(
    string Name,
    uint TypeId,
    string Species,
    int MaximumHealth,
    float Range,
    bool HasShield,
    string MetaState,
    Func<string, MonsterValue?>? Setting = null)
{
    internal bool TryResolve(string token, out MonsterValue value)
    {
        if (token.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            value = MonsterValue.FromBoolean(true);
            return true;
        }
        if (token.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            value = MonsterValue.FromBoolean(false);
            return true;
        }
        if (token.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            value = MonsterValue.FromText(Name);
            return true;
        }
        if (token.Equals("typeid", StringComparison.OrdinalIgnoreCase))
        {
            value = MonsterValue.FromNumber(TypeId);
            return true;
        }
        if (token.Equals("species", StringComparison.OrdinalIgnoreCase))
        {
            value = MonsterValue.FromText(Species);
            return true;
        }
        if (token.Equals("maxhp", StringComparison.OrdinalIgnoreCase))
        {
            value = MonsterValue.FromNumber(MaximumHealth);
            return true;
        }
        if (token.Equals("range", StringComparison.OrdinalIgnoreCase))
        {
            value = MonsterValue.FromNumber(Range);
            return true;
        }
        if (token.Equals("hasshield", StringComparison.OrdinalIgnoreCase))
        {
            value = MonsterValue.FromBoolean(HasShield);
            return true;
        }
        if (token.Equals("metastate", StringComparison.OrdinalIgnoreCase))
        {
            value = MonsterValue.FromText(MetaState);
            return true;
        }

        const string settingPrefix = "setting_";
        if (token.StartsWith(settingPrefix, StringComparison.Ordinal)
            && Setting?.Invoke(token[settingPrefix.Length..]) is { } setting)
        {
            value = setting;
            return true;
        }

        value = default;
        return false;
    }
}

internal sealed class MonsterExpressionException(string message)
    : FormatException(message);

internal sealed class MonsterExpression
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(25);

    private readonly Node _root;

    private MonsterExpression(string source, Node root)
    {
        Source = source;
        _root = root;
    }

    public string Source { get; }
    public bool IsDynamic => _root.IsDynamic;

    public static MonsterExpression Compile(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string normalized = source.Trim();
        if (normalized.Length == 0)
            throw new MonsterExpressionException("Monster expression is empty.");
        var parser = new Parser(normalized);
        Node root = parser.Parse();
        return new MonsterExpression(normalized, root);
    }

    public bool TryEvaluate(
        in MonsterExpressionContext context,
        out MonsterValue value,
        out string? error)
    {
        try
        {
            value = _root.Evaluate(context);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is MonsterExpressionException
            or RegexMatchTimeoutException
            or ArgumentException
            or OverflowException
            or DivideByZeroException)
        {
            value = default;
            error = ex.Message;
            return false;
        }
    }

    public bool IsMatch(in MonsterExpressionContext context, out string? error)
    {
        if (!TryEvaluate(context, out MonsterValue result, out error))
            return false;
        return result.Kind switch
        {
            MonsterValueKind.Boolean => result.Boolean,
            MonsterValueKind.Text => string.Equals(
                result.Text.Trim(),
                context.Name.Trim(),
                StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private abstract class Node(bool isDynamic)
    {
        internal bool IsDynamic { get; } = isDynamic;
        internal abstract MonsterValue Evaluate(in MonsterExpressionContext context);
    }

    private sealed class NumberNode(double value) : Node(false)
    {
        internal override MonsterValue Evaluate(in MonsterExpressionContext context) =>
            MonsterValue.FromNumber(value);
    }

    private sealed class AtomNode(string token) : Node(IsDynamicToken(token))
    {
        internal override MonsterValue Evaluate(in MonsterExpressionContext context) =>
            context.TryResolve(token, out MonsterValue value)
                ? value
                : MonsterValue.FromText(token.Trim());

        private static bool IsDynamicToken(string value) =>
            value.Equals("range", StringComparison.OrdinalIgnoreCase)
            || value.Equals("hasshield", StringComparison.OrdinalIgnoreCase)
            || value.Equals("metastate", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("setting_", StringComparison.Ordinal);
    }

    private sealed class BinaryNode(TokenKind operation, Node left, Node right)
        : Node(left.IsDynamic || right.IsDynamic)
    {
        internal override MonsterValue Evaluate(in MonsterExpressionContext context)
        {
            MonsterValue lhs = left.Evaluate(context);
            if (operation == TokenKind.And)
            {
                bool l = RequireBoolean(lhs, "&&");
                return !l
                    ? MonsterValue.FromBoolean(false)
                    : MonsterValue.FromBoolean(
                        RequireBoolean(right.Evaluate(context), "&&"));
            }
            if (operation == TokenKind.Or)
            {
                bool l = RequireBoolean(lhs, "||");
                return l
                    ? MonsterValue.FromBoolean(true)
                    : MonsterValue.FromBoolean(
                        RequireBoolean(right.Evaluate(context), "||"));
            }

            MonsterValue rhs = right.Evaluate(context);
            return operation switch
            {
                TokenKind.Modulo => MonsterValue.FromNumber(
                    (long)RequireNumber(lhs, "%") % (long)RequireNonZero(rhs, "%")),
                TokenKind.Divide => MonsterValue.FromNumber(
                    RequireNumber(lhs, "/") / RequireNonZero(rhs, "/")),
                TokenKind.Multiply => MonsterValue.FromNumber(
                    RequireNumber(lhs, "*") * RequireNumber(rhs, "*")),
                TokenKind.Add => Add(lhs, rhs),
                TokenKind.Subtract => MonsterValue.FromNumber(
                    RequireNumber(lhs, "-") - RequireNumber(rhs, "-")),
                TokenKind.Regex => RegexMatch(lhs, rhs),
                TokenKind.Equal => Compare(lhs, rhs, comparison => comparison == 0),
                TokenKind.NotEqual => Compare(lhs, rhs, comparison => comparison != 0),
                TokenKind.Greater => Compare(lhs, rhs, comparison => comparison > 0),
                TokenKind.Less => Compare(lhs, rhs, comparison => comparison < 0),
                TokenKind.GreaterOrEqual => Compare(lhs, rhs, comparison => comparison >= 0),
                TokenKind.LessOrEqual => Compare(lhs, rhs, comparison => comparison <= 0),
                _ => throw new MonsterExpressionException(
                    $"Unsupported monster-expression operator {operation}."),
            };
        }

        private static MonsterValue Add(MonsterValue left, MonsterValue right)
        {
            RequireSameType(left, right, "+");
            return left.Kind switch
            {
                MonsterValueKind.Number => MonsterValue.FromNumber(
                    left.Number + right.Number),
                MonsterValueKind.Text => MonsterValue.FromText(
                    left.Text + right.Text),
                _ => throw TypeError("+", left.Kind),
            };
        }

        private static MonsterValue RegexMatch(
            MonsterValue left,
            MonsterValue right)
        {
            RequireSameType(left, right, "#");
            if (left.Kind != MonsterValueKind.Text)
                throw TypeError("#", left.Kind);
            return MonsterValue.FromBoolean(Regex.IsMatch(
                left.Text,
                right.Text,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout));
        }

        private static MonsterValue Compare(
            MonsterValue left,
            MonsterValue right,
            Func<int, bool> predicate)
        {
            RequireSameType(left, right, "comparison");
            int comparison = left.Kind switch
            {
                MonsterValueKind.Number => left.Number.CompareTo(right.Number),
                MonsterValueKind.Text => string.Compare(
                    left.Text,
                    right.Text,
                    StringComparison.OrdinalIgnoreCase),
                MonsterValueKind.Boolean => left.Boolean.CompareTo(right.Boolean),
                _ => throw TypeError("comparison", left.Kind),
            };
            return MonsterValue.FromBoolean(predicate(comparison));
        }

        private static void RequireSameType(
            MonsterValue left,
            MonsterValue right,
            string operation)
        {
            if (left.Kind != right.Kind)
            {
                throw new MonsterExpressionException(
                    $"Operator {operation} requires matching operand types; "
                    + $"received {left.Kind} and {right.Kind}.");
            }
        }

        private static double RequireNumber(MonsterValue value, string operation)
        {
            if (value.Kind != MonsterValueKind.Number)
                throw TypeError(operation, value.Kind);
            return value.Number;
        }

        private static double RequireNonZero(MonsterValue value, string operation)
        {
            double number = RequireNumber(value, operation);
            if (number == 0d)
                throw new DivideByZeroException($"Operator {operation} divided by zero.");
            return number;
        }

        private static bool RequireBoolean(MonsterValue value, string operation)
        {
            if (value.Kind != MonsterValueKind.Boolean)
                throw TypeError(operation, value.Kind);
            return value.Boolean;
        }

        private static MonsterExpressionException TypeError(
            string operation,
            MonsterValueKind actual) => new(
                $"Operator {operation} cannot be applied to {actual}.");
    }

    private enum TokenKind
    {
        End,
        Atom,
        Number,
        LeftParen,
        RightParen,
        Modulo,
        Divide,
        Multiply,
        Add,
        Subtract,
        Regex,
        NotEqual,
        Equal,
        Greater,
        Less,
        GreaterOrEqual,
        LessOrEqual,
        And,
        Or,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Offset);

    private sealed class Lexer(string source)
    {
        private int _offset;

        internal Token Next()
        {
            while (_offset < source.Length && char.IsWhiteSpace(source[_offset]))
                _offset++;
            if (_offset >= source.Length)
                return new Token(TokenKind.End, string.Empty, _offset);

            int start = _offset;
            char current = source[_offset];
            if (TryOperator(out Token operation))
                return operation;

            if (char.IsDigit(current)
                || (current == '.'
                    && _offset + 1 < source.Length
                    && char.IsDigit(source[_offset + 1])))
            {
                _offset++;
                while (_offset < source.Length
                    && (char.IsDigit(source[_offset]) || source[_offset] == '.'))
                {
                    _offset++;
                }
                string number = source[start.._offset];
                if (!double.TryParse(
                    number,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out _))
                {
                    throw new MonsterExpressionException(
                        $"Invalid number '{number}' at offset {start}.");
                }
                return new Token(TokenKind.Number, number, start);
            }

            var text = new StringBuilder();
            while (_offset < source.Length)
            {
                current = source[_offset];
                if (current == '\\')
                {
                    if (_offset + 1 >= source.Length)
                    {
                        throw new MonsterExpressionException(
                            $"Trailing escape at offset {_offset}.");
                    }
                    text.Append(source[_offset + 1]);
                    _offset += 2;
                    continue;
                }
                if (IsOperatorStart(current) || char.IsDigit(current))
                    break;
                text.Append(current);
                _offset++;
            }

            string atom = text.ToString().Trim();
            if (atom.Length == 0)
            {
                throw new MonsterExpressionException(
                    $"Unexpected character '{source[_offset]}' at offset {_offset}; "
                    + "digits and punctuation in VTank strings must be escaped.");
            }
            return new Token(TokenKind.Atom, atom, start);
        }

        private bool TryOperator(out Token token)
        {
            int start = _offset;
            char c = source[_offset];
            TokenKind kind;
            int length = 1;
            if (_offset + 1 < source.Length)
            {
                string pair = source.Substring(_offset, 2);
                kind = pair switch
                {
                    "!=" => TokenKind.NotEqual,
                    "==" => TokenKind.Equal,
                    ">=" => TokenKind.GreaterOrEqual,
                    "<=" => TokenKind.LessOrEqual,
                    "&&" => TokenKind.And,
                    "||" => TokenKind.Or,
                    _ => TokenKind.End,
                };
                if (kind != TokenKind.End)
                {
                    length = 2;
                    _offset += length;
                    token = new Token(kind, pair, start);
                    return true;
                }
            }

            kind = c switch
            {
                '(' => TokenKind.LeftParen,
                ')' => TokenKind.RightParen,
                '%' => TokenKind.Modulo,
                '/' => TokenKind.Divide,
                '*' => TokenKind.Multiply,
                '+' => TokenKind.Add,
                '-' => TokenKind.Subtract,
                '#' => TokenKind.Regex,
                '>' => TokenKind.Greater,
                '<' => TokenKind.Less,
                _ => TokenKind.End,
            };
            if (kind == TokenKind.End)
            {
                token = default;
                return false;
            }
            _offset += length;
            token = new Token(kind, c.ToString(), start);
            return true;
        }

        private static bool IsOperatorStart(char value) =>
            value is '(' or ')' or '%' or '/' or '*' or '+' or '-' or '#'
                or '!' or '=' or '>' or '<' or '&' or '|';
    }

    private sealed class Parser
    {
        private readonly Lexer _lexer;
        private Token _current;

        internal Parser(string source)
        {
            _lexer = new Lexer(source);
            _current = _lexer.Next();
        }

        internal Node Parse()
        {
            Node result = ParseOr();
            if (_current.Kind != TokenKind.End)
            {
                throw new MonsterExpressionException(
                    $"Unexpected token '{_current.Text}' at offset {_current.Offset}.");
            }
            return result;
        }

        private Node ParseOr() => ParseLeftAssociative(ParseAnd, TokenKind.Or);
        private Node ParseAnd() => ParseLeftAssociative(ParseComparison, TokenKind.And);

        private Node ParseComparison() => ParseLeftAssociative(
            ParseRegex,
            TokenKind.NotEqual,
            TokenKind.Equal,
            TokenKind.Greater,
            TokenKind.Less,
            TokenKind.GreaterOrEqual,
            TokenKind.LessOrEqual);

        private Node ParseRegex() => ParseLeftAssociative(ParseSubtract, TokenKind.Regex);
        private Node ParseSubtract() => ParseLeftAssociative(ParseAdd, TokenKind.Subtract);
        private Node ParseAdd() => ParseLeftAssociative(ParseMultiply, TokenKind.Add);
        private Node ParseMultiply() => ParseLeftAssociative(ParseDivide, TokenKind.Multiply);
        private Node ParseDivide() => ParseLeftAssociative(ParseModulo, TokenKind.Divide);
        private Node ParseModulo() => ParseLeftAssociative(ParsePrimary, TokenKind.Modulo);

        private Node ParseLeftAssociative(
            Func<Node> operand,
            params TokenKind[] operations)
        {
            Node left = operand();
            while (operations.Contains(_current.Kind))
            {
                TokenKind operation = _current.Kind;
                Advance();
                left = new BinaryNode(operation, left, operand());
            }
            return left;
        }

        private Node ParsePrimary()
        {
            Token token = _current;
            switch (token.Kind)
            {
                case TokenKind.Number:
                    Advance();
                    return new NumberNode(double.Parse(
                        token.Text,
                        CultureInfo.InvariantCulture));
                case TokenKind.Atom:
                    Advance();
                    return new AtomNode(token.Text);
                case TokenKind.LeftParen:
                    Advance();
                    Node nested = ParseOr();
                    Require(TokenKind.RightParen, "Closing ')' expected");
                    Advance();
                    return nested;
                default:
                    throw new MonsterExpressionException(
                        $"Operand expected at offset {token.Offset}; found '{token.Text}'.");
            }
        }

        private void Require(TokenKind kind, string message)
        {
            if (_current.Kind != kind)
            {
                throw new MonsterExpressionException(
                    $"{message} at offset {_current.Offset}.");
            }
        }

        private void Advance() => _current = _lexer.Next();
    }
}
