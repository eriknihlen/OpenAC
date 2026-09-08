using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AcDream.Plugins.MossTank.Expressions;

internal sealed class ExpressionProgram
{
    private readonly Node[] _statements;

    private ExpressionProgram(Node[] statements) => _statements = statements;

    public static ExpressionProgram Compile(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ExpressionParseException("Expression is empty", 0);
        return new ExpressionProgram(new Parser(source).ParseProgram());
    }

    public ExpressionValue Evaluate(ExpressionEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ExpressionValue result = ExpressionValue.Zero;
        foreach (Node statement in _statements)
            result = statement.Evaluate(context);
        return result;
    }

    private abstract class Node(int offset)
    {
        protected int Offset { get; } = offset;
        internal abstract ExpressionValue Evaluate(ExpressionEvaluationContext context);
    }

    private sealed class LiteralNode(ExpressionValue value, int offset) : Node(offset)
    {
        internal override ExpressionValue Evaluate(ExpressionEvaluationContext context)
        {
            context.Step(Offset);
            return value;
        }
    }

    private sealed class VariableNode(
        ExpressionVariableScope scope,
        Node name,
        int offset) : Node(offset)
    {
        public ExpressionVariableScope Scope { get; } = scope;

        internal override ExpressionValue Evaluate(ExpressionEvaluationContext context)
        {
            context.Step(Offset);
            return context.State.Get(Scope, ResolveName(context));
        }

        public ExpressionValue Set(
            ExpressionEvaluationContext context,
            ExpressionValue value) =>
            context.State.Set(Scope, ResolveName(context), value);

        private string ResolveName(ExpressionEvaluationContext context) =>
            name.Evaluate(context).ToDisplayString();
    }

    private sealed class AssignmentNode(
        VariableNode variable,
        Node value,
        int offset) : Node(offset)
    {
        internal override ExpressionValue Evaluate(ExpressionEvaluationContext context)
        {
            context.Step(Offset);
            ExpressionValue result = value.Evaluate(context);
            return variable.Set(context, result);
        }
    }

    private sealed class FunctionNode(
        string name,
        Node[] arguments,
        int offset) : Node(offset)
    {
        internal override ExpressionValue Evaluate(ExpressionEvaluationContext context)
        {
            var values = new ExpressionValue[arguments.Length];
            for (int index = 0; index < arguments.Length; index++)
                values[index] = arguments[index].Evaluate(context);
            return context.Invoke(name, values, Offset);
        }
    }

    private sealed class UnaryNode(TokenKind operation, Node operand, int offset)
        : Node(offset)
    {
        internal override ExpressionValue Evaluate(ExpressionEvaluationContext context)
        {
            context.Step(Offset);
            ExpressionValue value = operand.Evaluate(context);
            return operation switch
            {
                TokenKind.Minus => ExpressionValue.Number(
                    -value.AsNumber("unary '-'")),
                TokenKind.Tilde => ExpressionValue.Number(
                    ~value.AsInt32("bitwise complement")),
                _ => throw new ExpressionEvaluationException(
                    $"Unsupported unary operator {operation}", Offset),
            };
        }
    }

    private sealed class BinaryNode(
        TokenKind operation,
        Node left,
        Node right,
        int offset) : Node(offset)
    {
        internal override ExpressionValue Evaluate(ExpressionEvaluationContext context)
        {
            context.Step(Offset);
            ExpressionValue lhs = left.Evaluate(context);
            if (operation == TokenKind.AndAnd)
                return lhs.IsTruthy ? right.Evaluate(context) : ExpressionValue.Zero;
            if (operation == TokenKind.OrOr)
                return lhs.IsTruthy ? lhs : right.Evaluate(context);

            ExpressionValue rhs = right.Evaluate(context);
            return operation switch
            {
                TokenKind.Plus => Add(lhs, rhs),
                TokenKind.Minus => Subtract(lhs, rhs),
                TokenKind.Star => ExpressionValue.Number(
                    lhs.AsNumber("multiplication") * rhs.AsNumber("multiplication")),
                TokenKind.Slash => ExpressionValue.Number(
                    lhs.AsNumber("division") / rhs.AsNumber("division")),
                TokenKind.Percent => ExpressionValue.Number(
                    lhs.AsNumber("modulo") % rhs.AsNumber("modulo")),
                TokenKind.Caret => ExpressionValue.Number(Math.Pow(
                    lhs.AsNumber("power"), rhs.AsNumber("power"))),
                TokenKind.ShiftLeft => ExpressionValue.Number(
                    lhs.AsInt32("left shift") << rhs.AsInt32("left shift")),
                TokenKind.ShiftRight => ExpressionValue.Number(
                    lhs.AsInt32("right shift") >> rhs.AsInt32("right shift")),
                TokenKind.Ampersand => ExpressionValue.Number(
                    lhs.AsInt32("bitwise and") & rhs.AsInt32("bitwise and")),
                TokenKind.Pipe => ExpressionValue.Number(
                    lhs.AsInt32("bitwise or") | rhs.AsInt32("bitwise or")),
                TokenKind.Hash => RegexMatch(context, lhs, rhs),
                TokenKind.EqualEqual => ExpressionValue.Boolean(lhs.Equals(rhs)),
                TokenKind.BangEqual => ExpressionValue.Boolean(!lhs.Equals(rhs)),
                TokenKind.Less => Compare(lhs, rhs, static comparison => comparison < 0),
                TokenKind.LessEqual => Compare(lhs, rhs, static comparison => comparison <= 0),
                TokenKind.Greater => Compare(lhs, rhs, static comparison => comparison > 0),
                TokenKind.GreaterEqual => Compare(lhs, rhs, static comparison => comparison >= 0),
                _ => throw new ExpressionEvaluationException(
                    $"Unsupported binary operator {operation}", Offset),
            };
        }

        private static ExpressionValue Add(
            in ExpressionValue left,
            in ExpressionValue right)
        {
            if (left.Kind == ExpressionValueKind.Number
                || left.Kind == ExpressionValueKind.Boolean)
            {
                return ExpressionValue.Number(
                    left.AsNumber("addition") + right.AsNumber("addition"));
            }
            if (left.Kind == ExpressionValueKind.String)
            {
                return ExpressionValue.String(
                    left.AsString("concatenation") + right.ToDisplayString());
            }
            throw new ExpressionEvaluationException(
                $"Unable to add {left.Kind} to {right.Kind}.");
        }

        private static ExpressionValue Subtract(
            in ExpressionValue left,
            in ExpressionValue right)
        {
            if (left.Kind is ExpressionValueKind.Number
                    or ExpressionValueKind.Boolean
                && right.Kind is ExpressionValueKind.Number
                    or ExpressionValueKind.Boolean)
            {
                return ExpressionValue.Number(
                    left.AsNumber("subtraction") - right.AsNumber("subtraction"));
            }
            if (left.Kind == ExpressionValueKind.String
                && right.Kind == ExpressionValueKind.String)
            {
                return ExpressionValue.String(
                    left.AsString() + "-" + right.AsString());
            }
            throw new ExpressionEvaluationException(
                $"Unable to subtract {right.Kind} from {left.Kind}.");
        }

        private static ExpressionValue Compare(
            in ExpressionValue left,
            in ExpressionValue right,
            Func<int, bool> predicate)
        {
            double lhs = left.AsNumber("comparison");
            double rhs = right.AsNumber("comparison");
            return ExpressionValue.Boolean(predicate(lhs.CompareTo(rhs)));
        }

        private static ExpressionValue RegexMatch(
            ExpressionEvaluationContext context,
            in ExpressionValue left,
            in ExpressionValue right)
        {
            var regex = new Regex(
                right.ToDisplayString(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            Match match = regex.Match(left.ToDisplayString());
            foreach (string groupName in regex.GetGroupNames())
            {
                string variableName = "capturegroup_" + groupName;
                Group group = match.Groups[groupName];
                if (group.Success)
                {
                    context.State.Set(
                        ExpressionVariableScope.Session,
                        variableName,
                        ExpressionValue.String(group.Value));
                }
                else
                {
                    context.State.Clear(
                        ExpressionVariableScope.Session,
                        variableName);
                }
            }
            return ExpressionValue.Boolean(match.Success);
        }
    }

    private sealed class IndexNode(
        Node source,
        Node? start,
        Node? end,
        bool isSlice,
        int offset) : Node(offset)
    {
        internal override ExpressionValue Evaluate(ExpressionEvaluationContext context)
        {
            context.Step(Offset);
            ExpressionValue value = source.Evaluate(context);
            if (value.Kind == ExpressionValueKind.Dictionary)
            {
                if (isSlice)
                {
                    throw new ExpressionEvaluationException(
                        "Range indices are not supported with dictionaries",
                        Offset);
                }
                string key = (start?.Evaluate(context) ?? ExpressionValue.Zero)
                    .AsString("dictionary index");
                return value.AsDictionary().Items.TryGetValue(
                    key,
                    out ExpressionValue found)
                        ? found
                        : ExpressionValue.Zero;
            }

            int length = value.Kind switch
            {
                ExpressionValueKind.List => value.AsList().Items.Count,
                ExpressionValueKind.String => value.AsString().Length,
                _ => throw new ExpressionEvaluationException(
                    $"{value.Kind} does not support index access",
                    Offset),
            };
            int first = ResolveIndex(context, start, length, 0, allowEnd: isSlice);
            if (!isSlice)
            {
                return value.Kind == ExpressionValueKind.List
                    ? value.AsList().Items[first]
                    : ExpressionValue.String(value.AsString().Substring(first, 1));
            }
            int last = ResolveIndex(context, end, length, length, allowEnd: true);
            int count = Math.Max(0, last - first);
            return value.Kind == ExpressionValueKind.List
                ? ExpressionValue.List(new ExpressionList(
                    value.AsList().Items.Skip(first).Take(count)))
                : ExpressionValue.String(value.AsString().Substring(first, count));
        }

        private int ResolveIndex(
            ExpressionEvaluationContext context,
            Node? expression,
            int length,
            int defaultValue,
            bool allowEnd)
        {
            int index = expression is null
                ? defaultValue
                : expression.Evaluate(context).AsInt32("index");
            if (index < 0)
                index += length;
            int maximum = allowEnd ? length : length - 1;
            if (index < 0 || index > maximum)
            {
                throw new ExpressionEvaluationException(
                    $"Index {index} is outside 0..{maximum}",
                    Offset);
            }
            return index;
        }
    }

    private enum TokenKind
    {
        End,
        Number,
        HexNumber,
        String,
        True,
        False,
        LeftParen,
        RightParen,
        LeftBracket,
        RightBracket,
        LeftBrace,
        RightBrace,
        Comma,
        Semicolon,
        Colon,
        Dollar,
        At,
        Ampersand,
        Pipe,
        Tilde,
        Plus,
        Minus,
        Star,
        Slash,
        Percent,
        Caret,
        Hash,
        Equal,
        EqualEqual,
        BangEqual,
        Less,
        LessEqual,
        Greater,
        GreaterEqual,
        ShiftLeft,
        ShiftRight,
        AndAnd,
        OrOr,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Offset);

    private sealed class Lexer(string source)
    {
        private int _offset;

        public Token Next()
        {
            while (_offset < source.Length && char.IsWhiteSpace(source[_offset]))
                _offset++;
            if (_offset >= source.Length)
                return new Token(TokenKind.End, string.Empty, _offset);

            int start = _offset;
            char current = source[_offset];
            if (current is '`' or '\'' or '"')
                return ReadQuoted(current, start);
            if (char.IsDigit(current)
                || (current == '.'
                    && _offset + 1 < source.Length
                    && char.IsDigit(source[_offset + 1])))
            {
                return ReadNumber(start);
            }
            if (TryOperator(out Token operation))
                return operation;

            while (_offset < source.Length && !IsDelimiter(source[_offset]))
                _offset++;
            string text = source[start.._offset].Trim();
            if (text.Length == 0)
            {
                throw new ExpressionParseException(
                    $"Unexpected character '{source[start]}'",
                    start);
            }
            return text.Equals("true", StringComparison.OrdinalIgnoreCase)
                ? new Token(TokenKind.True, text, start)
                : text.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? new Token(TokenKind.False, text, start)
                    : new Token(TokenKind.String, Unescape(text), start);
        }

        private Token ReadQuoted(char delimiter, int start)
        {
            _offset++;
            var built = new StringBuilder();
            while (_offset < source.Length)
            {
                char value = source[_offset++];
                if (value == delimiter)
                    return new Token(TokenKind.String, built.ToString(), start);
                if (value == '\\' && _offset < source.Length)
                    value = source[_offset++];
                built.Append(value);
            }
            throw new ExpressionParseException("Unterminated string", start);
        }

        private Token ReadNumber(int start)
        {
            if (_offset + 1 < source.Length
                && source[_offset] == '0'
                && source[_offset + 1] is 'x' or 'X')
            {
                _offset += 2;
                int digits = _offset;
                while (_offset < source.Length && Uri.IsHexDigit(source[_offset]))
                    _offset++;
                if (_offset == digits)
                    throw new ExpressionParseException("Hexadecimal digits expected", start);
                return new Token(TokenKind.HexNumber, source[digits.._offset], start);
            }

            bool dot = false;
            while (_offset < source.Length)
            {
                char value = source[_offset];
                if (char.IsDigit(value))
                {
                    _offset++;
                    continue;
                }
                if (value == '.' && !dot)
                {
                    dot = true;
                    _offset++;
                    continue;
                }
                break;
            }
            return new Token(TokenKind.Number, source[start.._offset], start);
        }

        private bool TryOperator(out Token token)
        {
            int start = _offset;
            if (_offset + 1 < source.Length)
            {
                string pair = source.Substring(_offset, 2);
                TokenKind pairKind = pair switch
                {
                    "==" => TokenKind.EqualEqual,
                    "!=" => TokenKind.BangEqual,
                    "<=" => TokenKind.LessEqual,
                    ">=" => TokenKind.GreaterEqual,
                    "<<" => TokenKind.ShiftLeft,
                    ">>" => TokenKind.ShiftRight,
                    "&&" => TokenKind.AndAnd,
                    "||" => TokenKind.OrOr,
                    _ => TokenKind.End,
                };
                if (pairKind != TokenKind.End)
                {
                    _offset += 2;
                    token = new Token(pairKind, pair, start);
                    return true;
                }
            }

            TokenKind kind = source[_offset] switch
            {
                '(' => TokenKind.LeftParen,
                ')' => TokenKind.RightParen,
                '[' => TokenKind.LeftBracket,
                ']' => TokenKind.RightBracket,
                '{' => TokenKind.LeftBrace,
                '}' => TokenKind.RightBrace,
                ',' => TokenKind.Comma,
                ';' => TokenKind.Semicolon,
                ':' => TokenKind.Colon,
                '$' => TokenKind.Dollar,
                '@' => TokenKind.At,
                '&' => TokenKind.Ampersand,
                '|' => TokenKind.Pipe,
                '~' => TokenKind.Tilde,
                '+' => TokenKind.Plus,
                '-' => TokenKind.Minus,
                '*' => TokenKind.Star,
                '/' => TokenKind.Slash,
                '%' => TokenKind.Percent,
                '^' => TokenKind.Caret,
                '#' => TokenKind.Hash,
                '=' => TokenKind.Equal,
                '<' => TokenKind.Less,
                '>' => TokenKind.Greater,
                _ => TokenKind.End,
            };
            if (kind == TokenKind.End)
            {
                token = default;
                return false;
            }
            _offset++;
            token = new Token(kind, source[start].ToString(), start);
            return true;
        }

        private static bool IsDelimiter(char value) =>
            char.IsWhiteSpace(value)
                ? false
                : value is '(' or ')' or '[' or ']' or '{' or '}'
                    or ',' or ';' or ':' or '$' or '@' or '&' or '|'
                    or '~' or '+' or '-' or '*' or '/' or '%' or '^'
                    or '#' or '=' or '!' or '<' or '>' or '`' or '\'' or '"';

        private static string Unescape(string value)
        {
            if (!value.Contains('\\', StringComparison.Ordinal))
                return value;
            var built = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current == '\\' && index + 1 < value.Length)
                    current = value[++index];
                built.Append(current);
            }
            return built.ToString();
        }
    }

    private sealed class Parser
    {
        private readonly Lexer _lexer;
        private Token _current;

        public Parser(string source)
        {
            _lexer = new Lexer(source);
            _current = _lexer.Next();
        }

        public Node[] ParseProgram()
        {
            var statements = new List<Node>();
            while (_current.Kind != TokenKind.End)
            {
                statements.Add(ParseAssignment());
                if (_current.Kind == TokenKind.Semicolon)
                {
                    Advance();
                    continue;
                }
                if (_current.Kind != TokenKind.End)
                {
                    throw new ExpressionParseException(
                        $"Unexpected token '{_current.Text}'",
                        _current.Offset);
                }
            }
            return statements.ToArray();
        }

        private Node ParseAssignment()
        {
            Node left = ParseOr();
            if (_current.Kind != TokenKind.Equal)
                return left;
            Token operation = _current;
            Advance();
            if (left is not VariableNode variable)
            {
                throw new ExpressionParseException(
                    "Only variables may appear on the left of '='",
                    operation.Offset);
            }
            return new AssignmentNode(variable, ParseAssignment(), operation.Offset);
        }

        private Node ParseOr() => ParseLeft(ParseAnd, TokenKind.OrOr);
        private Node ParseAnd() => ParseLeft(ParseComparison, TokenKind.AndAnd);
        private Node ParseComparison() => ParseLeft(
            ParseRegex,
            TokenKind.EqualEqual,
            TokenKind.BangEqual,
            TokenKind.Less,
            TokenKind.LessEqual,
            TokenKind.Greater,
            TokenKind.GreaterEqual);
        private Node ParseRegex() => ParseLeft(ParseBitwiseOr, TokenKind.Hash);
        private Node ParseBitwiseOr() => ParseLeft(ParseBitwiseAnd, TokenKind.Pipe);
        private Node ParseBitwiseAnd() => ParseLeft(ParseShift, TokenKind.Ampersand);
        private Node ParseShift() => ParseLeft(
            ParseAdditive,
            TokenKind.ShiftLeft,
            TokenKind.ShiftRight);
        private Node ParseAdditive() => ParseLeft(
            ParseMultiplicative,
            TokenKind.Plus,
            TokenKind.Minus);
        private Node ParseMultiplicative() => ParseLeft(
            ParsePower,
            TokenKind.Star,
            TokenKind.Slash,
            TokenKind.Percent);

        private Node ParsePower()
        {
            Node left = ParseUnary();
            if (_current.Kind != TokenKind.Caret)
                return left;
            Token operation = _current;
            Advance();
            return new BinaryNode(
                operation.Kind,
                left,
                ParsePower(),
                operation.Offset);
        }

        private Node ParseUnary()
        {
            if (_current.Kind is not (TokenKind.Minus or TokenKind.Tilde))
                return ParsePostfix();
            Token operation = _current;
            Advance();
            return new UnaryNode(operation.Kind, ParseUnary(), operation.Offset);
        }

        private Node ParsePostfix()
        {
            Node source = ParsePrimary();
            while (_current.Kind == TokenKind.LeftBrace)
            {
                Token opening = _current;
                Advance();
                Node? start = null;
                Node? end = null;
                bool slice = false;
                if (_current.Kind != TokenKind.Colon
                    && _current.Kind != TokenKind.RightBrace)
                {
                    start = ParseAssignment();
                }
                if (_current.Kind == TokenKind.Colon)
                {
                    slice = true;
                    Advance();
                    if (_current.Kind != TokenKind.RightBrace)
                        end = ParseAssignment();
                }
                Require(TokenKind.RightBrace, "Closing '}' expected");
                source = new IndexNode(source, start, end, slice, opening.Offset);
            }
            return source;
        }

        private Node ParsePrimary()
        {
            Token token = _current;
            switch (token.Kind)
            {
                case TokenKind.Number:
                    Advance();
                    return new LiteralNode(
                        ExpressionValue.Number(double.Parse(
                            token.Text,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture)),
                        token.Offset);
                case TokenKind.HexNumber:
                    Advance();
                    return new LiteralNode(
                        ExpressionValue.Number(Convert.ToUInt32(
                            token.Text,
                            16)),
                        token.Offset);
                case TokenKind.True:
                case TokenKind.False:
                    Advance();
                    return new LiteralNode(
                        ExpressionValue.Boolean(token.Kind == TokenKind.True),
                        token.Offset);
                case TokenKind.String:
                    Advance();
                    if (_current.Kind != TokenKind.LeftBracket)
                    {
                        return new LiteralNode(
                            ExpressionValue.String(token.Text),
                            token.Offset);
                    }
                    return ParseFunction(token);
                case TokenKind.Dollar:
                case TokenKind.At:
                case TokenKind.Ampersand:
                    return ParseVariable();
                case TokenKind.LeftParen:
                    Advance();
                    Node nested = ParseAssignment();
                    Require(TokenKind.RightParen, "Closing ')' expected");
                    return nested;
                default:
                    throw new ExpressionParseException(
                        $"Expression expected; found '{token.Text}'",
                        token.Offset);
            }
        }

        private Node ParseFunction(Token name)
        {
            Require(TokenKind.LeftBracket, "Opening '[' expected");
            var arguments = new List<Node>();
            if (_current.Kind != TokenKind.RightBracket)
            {
                while (true)
                {
                    arguments.Add(ParseAssignment());
                    if (_current.Kind != TokenKind.Comma)
                        break;
                    Advance();
                }
            }
            Require(TokenKind.RightBracket, "Closing ']' expected");
            return new FunctionNode(name.Text, arguments.ToArray(), name.Offset);
        }

        private Node ParseVariable()
        {
            Token prefix = _current;
            Advance();
            if (_current.Kind is TokenKind.End
                or TokenKind.Comma
                or TokenKind.Semicolon
                or TokenKind.RightBracket
                or TokenKind.RightBrace
                or TokenKind.RightParen)
            {
                throw new ExpressionParseException(
                    "Variable name expected",
                    _current.Offset);
            }
            Node name = ParsePrimary();
            ExpressionVariableScope scope = prefix.Kind switch
            {
                TokenKind.Dollar => ExpressionVariableScope.Session,
                TokenKind.At => ExpressionVariableScope.Persistent,
                TokenKind.Ampersand => ExpressionVariableScope.Global,
                _ => throw new InvalidOperationException(),
            };
            return new VariableNode(scope, name, prefix.Offset);
        }

        private Node ParseLeft(
            Func<Node> operand,
            params TokenKind[] operations)
        {
            Node left = operand();
            while (operations.Contains(_current.Kind))
            {
                Token operation = _current;
                Advance();
                left = new BinaryNode(
                    operation.Kind,
                    left,
                    operand(),
                    operation.Offset);
            }
            return left;
        }

        private void Require(TokenKind expected, string message)
        {
            if (_current.Kind != expected)
                throw new ExpressionParseException(message, _current.Offset);
            Advance();
        }

        private void Advance() => _current = _lexer.Next();
    }
}
