namespace Rampside.Core.Gsx.Scripting;

internal abstract record Expr(int Line);

internal sealed record ConstExpr(object Value, int Line) : Expr(Line);

internal sealed record NameExpr(string Name, int Line) : Expr(Line);

internal sealed record TupleExpr(IReadOnlyList<Expr> Items, int Line) : Expr(Line);

internal sealed record ListExpr(IReadOnlyList<Expr> Items, int Line) : Expr(Line);

internal sealed record DictExpr(IReadOnlyList<(Expr Key, Expr Value)> Items, int Line) : Expr(Line);

internal sealed record AttrExpr(Expr Target, string Name, int Line) : Expr(Line);

internal sealed record CallExpr(Expr Function, IReadOnlyList<Expr> Args, int Line) : Expr(Line);

internal sealed record SubscriptExpr(Expr Target, Expr Index, int Line) : Expr(Line);

internal sealed record UnaryExpr(string Op, Expr Operand, int Line) : Expr(Line);

internal sealed record BinaryExpr(string Op, Expr Left, Expr Right, int Line) : Expr(Line);

/// <summary>A comparison chain: <c>a &lt; b &lt;= c</c>. Ops include "in", "not in", "is", "is not".</summary>
internal sealed record CompareExpr(Expr First, IReadOnlyList<(string Op, Expr Right)> Rest, int Line) : Expr(Line);

internal sealed record BoolExpr(bool IsAnd, Expr Left, Expr Right, int Line) : Expr(Line);

internal sealed record ConditionalExpr(Expr Test, Expr Then, Expr Else, int Line) : Expr(Line);

internal abstract record Stmt(int Line);

internal sealed record ExprStmt(Expr Value, int Line) : Stmt(Line);

/// <summary><c>a = b = value</c>; targets are names, tuples of names or subscripts.</summary>
internal sealed record AssignStmt(IReadOnlyList<Expr> Targets, Expr Value, int Line) : Stmt(Line);

/// <summary><c>a += value</c>.</summary>
internal sealed record AugAssignStmt(Expr Target, string Op, Expr Value, int Line) : Stmt(Line);

internal sealed record ReturnStmt(Expr? Value, int Line) : Stmt(Line);

internal sealed record IfStmt(Expr Test, IReadOnlyList<Stmt> Body, IReadOnlyList<Stmt> Else, int Line) : Stmt(Line);

/// <param name="Types">Exception types the clause catches; null = bare <c>except:</c>.</param>
internal sealed record ExceptClause(Expr? Types, string? Variable, IReadOnlyList<Stmt> Body);

internal sealed record TryStmt(
    IReadOnlyList<Stmt> Body,
    IReadOnlyList<ExceptClause> Handlers,
    IReadOnlyList<Stmt> Else,
    IReadOnlyList<Stmt> Finally,
    int Line) : Stmt(Line);

internal sealed record DefStmt(
    string Name,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<Expr?> Defaults,
    IReadOnlyList<Expr> Decorators,
    IReadOnlyList<Stmt> Body,
    int Line) : Stmt(Line);

internal sealed record GlobalStmt(IReadOnlyList<string> Names, int Line) : Stmt(Line);

internal sealed record PassStmt(int Line) : Stmt(Line);

/// <summary>
/// Parses the part of Python 2 that GSX airport scripts use: functions with decorators, if/elif/else, try/except,
/// assignments, return, global and pass; expressions without lambdas or comprehensions.
/// Loops, imports, classes and the like are refused, so a script can never run for long or reach the system.
/// </summary>
internal sealed class PyParser
{
    private static readonly HashSet<string> Unsupported = new(StringComparer.Ordinal)
    {
        "for", "while", "import", "from", "class", "lambda", "with", "exec", "print", "yield", "del", "assert", "raise", "async", "await", "nonlocal",
    };

    private readonly List<Token> _tokens;
    private int _pos;

    private PyParser(List<Token> tokens) => _tokens = tokens;

    /// <summary>
    /// The module's top-level statements. A statement that cannot be parsed is skipped up to the next top-level
    /// statement and listed in <paramref name="problems"/>; an error in the tokenizer fails the whole file.
    /// </summary>
    public static List<Stmt> ParseModule(string text, ICollection<string>? problems)
    {
        var parser = new PyParser(PyLexer.Tokenize(text));
        var statements = new List<Stmt>();
        while (parser.Peek.Kind != TokenKind.End)
        {
            if (parser.Peek.Kind is TokenKind.Newline or TokenKind.Dedent)
            {
                parser._pos++;
                continue;
            }

            try
            {
                statements.AddRange(parser.Statement());
            }
            catch (PySyntaxError ex)
            {
                problems?.Add(ex.Message);
                parser.SkipToTopLevel();
            }
        }

        return statements;
    }

    private Token Peek => _tokens[_pos];

    private Token PeekAt(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];

    private void SkipToTopLevel()
    {
        _pos++;
        while (!(Peek.StartsLine && Peek.LineIndent == 0) && Peek.Kind != TokenKind.End)
        {
            _pos++;
        }
    }

    private Token Take() => _tokens[_pos++];

    private bool TryTake(string op)
    {
        if (Peek.Is(op))
        {
            _pos++;
            return true;
        }

        return false;
    }

    private Token Expect(string op)
    {
        if (!Peek.Is(op))
        {
            throw Error($"expected '{op}', found {Peek}");
        }

        return Take();
    }

    private Token Expect(TokenKind kind)
    {
        if (Peek.Kind != kind)
        {
            throw Error($"expected {kind.ToString().ToLowerInvariant()}, found {Peek}");
        }

        return Take();
    }

    private PySyntaxError Error(string message) => new(Peek.Line, message);

    private List<Stmt> Statement()
    {
        var token = Peek;
        if (token.Kind == TokenKind.Indent)
        {
            throw Error("unexpected indent");
        }

        if (token.Kind == TokenKind.Name && Unsupported.Contains(token.Text))
        {
            throw Error($"'{token.Text}' is not supported");
        }

        switch (token.Text)
        {
            case "@" when token.Kind == TokenKind.Op:
            case "def" when token.Kind == TokenKind.Name:
                return [Def()];
            case "if" when token.Kind == TokenKind.Name:
                Take();
                return [If(token.Line)];
            case "try" when token.Kind == TokenKind.Name:
                return [Try()];
        }

        // Simple statements, separated by ';' on one line.
        var statements = new List<Stmt> { SimpleStatement() };
        while (TryTake(";"))
        {
            if (Peek.Kind == TokenKind.Newline)
            {
                break;
            }

            statements.Add(SimpleStatement());
        }

        Expect(TokenKind.Newline);
        return statements;
    }

    private Stmt SimpleStatement()
    {
        var token = Peek;
        if (token.Kind == TokenKind.Name)
        {
            if (Unsupported.Contains(token.Text))
            {
                throw Error($"'{token.Text}' is not supported");
            }

            switch (token.Text)
            {
                case "pass" or "continue" or "break":
                    if (token.Text != "pass")
                    {
                        throw Error($"'{token.Text}' is not supported");
                    }

                    Take();
                    return new PassStmt(token.Line);
                case "return":
                    Take();
                    return new ReturnStmt(Peek.Kind == TokenKind.Newline || Peek.Is(";") ? null : TestList(), token.Line);
                case "global":
                    Take();
                    var names = new List<string> { Expect(TokenKind.Name).Text };
                    while (TryTake(","))
                    {
                        names.Add(Expect(TokenKind.Name).Text);
                    }

                    return new GlobalStmt(names, token.Line);
            }
        }

        var first = TestList();
        if (Peek.Kind == TokenKind.Op && Peek.Text is "+=" or "-=" or "*=" or "/=")
        {
            var op = Take().Text[..1];
            CheckTarget(first);
            return new AugAssignStmt(first, op, TestList(), token.Line);
        }

        if (!Peek.Is("="))
        {
            return new ExprStmt(first, token.Line);
        }

        var targets = new List<Expr> { first };
        Expr value = first;
        while (TryTake("="))
        {
            value = TestList();
            targets.Add(value);
        }

        targets.RemoveAt(targets.Count - 1);
        foreach (var target in targets)
        {
            CheckTarget(target);
        }

        return new AssignStmt(targets, value, token.Line);
    }

    private void CheckTarget(Expr target)
    {
        switch (target)
        {
            case NameExpr or SubscriptExpr:
                return;
            case TupleExpr or ListExpr:
                foreach (var item in target is TupleExpr t ? t.Items : ((ListExpr)target).Items)
                {
                    CheckTarget(item);
                }

                return;
            default:
                throw new PySyntaxError(target.Line, "cannot assign to this expression");
        }
    }

    private DefStmt Def()
    {
        var decorators = new List<Expr>();
        while (TryTake("@"))
        {
            decorators.Add(Test());
            Expect(TokenKind.Newline);
        }

        var line = Expect("def").Line;
        var name = Expect(TokenKind.Name).Text;
        Expect("(");
        var parameters = new List<string>();
        var defaults = new List<Expr?>();
        while (!Peek.Is(")"))
        {
            if (Peek.Is("*") || Peek.Is("**"))
            {
                throw Error("*args and **kwargs are not supported");
            }

            parameters.Add(Expect(TokenKind.Name).Text);
            defaults.Add(TryTake("=") ? Test() : null);
            if (!TryTake(","))
            {
                break;
            }
        }

        Expect(")");
        return new DefStmt(name, parameters, defaults, decorators, Suite(), line);
    }

    private IfStmt If(int line)
    {
        var test = Test();
        var body = Suite();
        IReadOnlyList<Stmt> otherwise = Array.Empty<Stmt>();
        if (Peek.Is("elif"))
        {
            var elif = Take();
            otherwise = [If(elif.Line)];
        }
        else if (TryTake("else"))
        {
            otherwise = Suite();
        }

        return new IfStmt(test, body, otherwise, line);
    }

    private TryStmt Try()
    {
        var line = Expect("try").Line;
        var body = Suite();
        var handlers = new List<ExceptClause>();
        while (TryTake("except"))
        {
            Expr? types = null;
            string? variable = null;
            if (!Peek.Is(":"))
            {
                types = Test();
                if (TryTake("as") || TryTake(","))
                {
                    variable = Expect(TokenKind.Name).Text;
                }
            }

            handlers.Add(new ExceptClause(types, variable, Suite()));
        }

        IReadOnlyList<Stmt> otherwise = handlers.Count > 0 && TryTake("else") ? Suite() : Array.Empty<Stmt>();
        IReadOnlyList<Stmt> final = TryTake("finally") ? Suite() : Array.Empty<Stmt>();
        if (handlers.Count == 0 && final.Count == 0)
        {
            throw new PySyntaxError(line, "try without except or finally");
        }

        return new TryStmt(body, handlers, otherwise, final, line);
    }

    /// <summary>':' then simple statements on the same line, or an indented block.</summary>
    private List<Stmt> Suite()
    {
        Expect(":");
        if (Peek.Kind != TokenKind.Newline)
        {
            var simple = new List<Stmt> { SimpleStatement() };
            while (TryTake(";") && Peek.Kind != TokenKind.Newline)
            {
                simple.Add(SimpleStatement());
            }

            Expect(TokenKind.Newline);
            return simple;
        }

        Take();
        Expect(TokenKind.Indent);
        var body = new List<Stmt>();
        while (Peek.Kind != TokenKind.Dedent && Peek.Kind != TokenKind.End)
        {
            if (Peek.Kind == TokenKind.Newline)
            {
                Take();
                continue;
            }

            body.AddRange(Statement());
        }

        if (Peek.Kind == TokenKind.Dedent)
        {
            Take();
        }

        return body;
    }

    /// <summary>One or more expressions separated by commas; more than one (or a trailing comma) is a tuple.</summary>
    private Expr TestList()
    {
        var line = Peek.Line;
        var first = Test();
        if (!Peek.Is(","))
        {
            return first;
        }

        var items = new List<Expr> { first };
        while (TryTake(","))
        {
            if (EndsList(Peek))
            {
                break;
            }

            items.Add(Test());
        }

        return new TupleExpr(items, line);
    }

    private static bool EndsList(Token token) =>
        token.Kind is TokenKind.Newline or TokenKind.End || token.Is("=") || token.Is(")") || token.Is("]") || token.Is("}") || token.Is(";") || token.Is(":");

    private Expr Test()
    {
        var value = OrTest();
        if (Peek.Is("if"))
        {
            var line = Take().Line;
            var test = OrTest();
            Expect("else");
            return new ConditionalExpr(test, value, Test(), line);
        }

        return value;
    }

    private Expr OrTest()
    {
        var left = AndTest();
        while (Peek.Is("or"))
        {
            var line = Take().Line;
            left = new BoolExpr(false, left, AndTest(), line);
        }

        return left;
    }

    private Expr AndTest()
    {
        var left = NotTest();
        while (Peek.Is("and"))
        {
            var line = Take().Line;
            left = new BoolExpr(true, left, NotTest(), line);
        }

        return left;
    }

    private Expr NotTest()
    {
        if (Peek.Is("not"))
        {
            var line = Take().Line;
            return new UnaryExpr("not", NotTest(), line);
        }

        return Comparison();
    }

    private Expr Comparison()
    {
        var line = Peek.Line;
        var first = Arith();
        var rest = new List<(string, Expr)>();
        while (true)
        {
            string? op = null;
            if (Peek.Kind == TokenKind.Op && Peek.Text is "<" or ">" or "==" or "!=" or "<>" or "<=" or ">=")
            {
                op = Take().Text == "<>" ? "!=" : _tokens[_pos - 1].Text;
            }
            else if (Peek.Is("in"))
            {
                Take();
                op = "in";
            }
            else if (Peek.Is("not") && PeekAt(1).Is("in"))
            {
                _pos += 2;
                op = "not in";
            }
            else if (Peek.Is("is"))
            {
                Take();
                op = TryTake("not") ? "is not" : "is";
            }

            if (op is null)
            {
                break;
            }

            rest.Add((op, Arith()));
        }

        return rest.Count == 0 ? first : new CompareExpr(first, rest, line);
    }

    private Expr Arith()
    {
        var left = Term();
        while (Peek.Kind == TokenKind.Op && Peek.Text is "+" or "-")
        {
            var op = Take();
            left = new BinaryExpr(op.Text, left, Term(), op.Line);
        }

        return left;
    }

    private Expr Term()
    {
        var left = Factor();
        while (Peek.Kind == TokenKind.Op && Peek.Text is "*" or "/" or "%" or "//")
        {
            var op = Take();
            left = new BinaryExpr(op.Text, left, Factor(), op.Line);
        }

        return left;
    }

    private Expr Factor()
    {
        if (Peek.Kind == TokenKind.Op && Peek.Text is "-" or "+")
        {
            var op = Take();
            return new UnaryExpr(op.Text, Factor(), op.Line);
        }

        return Power();
    }

    private Expr Power()
    {
        var value = Trailers(Atom());
        if (Peek.Is("**"))
        {
            var line = Take().Line;
            return new BinaryExpr("**", value, Factor(), line);
        }

        return value;
    }

    private Expr Trailers(Expr value)
    {
        while (true)
        {
            var line = Peek.Line;
            if (TryTake("."))
            {
                value = new AttrExpr(value, Expect(TokenKind.Name).Text, line);
            }
            else if (TryTake("("))
            {
                var args = new List<Expr>();
                while (!Peek.Is(")"))
                {
                    if (Peek.Kind == TokenKind.Name && PeekAt(1).Is("="))
                    {
                        throw Error("keyword arguments are not supported");
                    }

                    args.Add(Test());
                    if (!TryTake(","))
                    {
                        break;
                    }
                }

                Expect(")");
                value = new CallExpr(value, args, line);
            }
            else if (TryTake("["))
            {
                if (Peek.Is(":"))
                {
                    throw Error("slices are not supported");
                }

                var index = TestList();
                if (Peek.Is(":"))
                {
                    throw Error("slices are not supported");
                }

                Expect("]");
                value = new SubscriptExpr(value, index, line);
            }
            else
            {
                return value;
            }
        }
    }

    private Expr Atom()
    {
        var token = Peek;
        switch (token.Kind)
        {
            case TokenKind.Number:
                Take();
                return new ConstExpr(token.Value!, token.Line);
            case TokenKind.String:
                Take();
                var text = (string)token.Value!;
                while (Peek.Kind == TokenKind.String)
                {
                    text += (string)Take().Value!;
                }

                return new ConstExpr(text, token.Line);
            case TokenKind.Name:
                if (Unsupported.Contains(token.Text) || token.Text is "if" or "else" or "elif" or "def" or "return" or "and" or "or" or "not" or "in" or "is")
                {
                    throw Error($"'{token.Text}' is not supported here");
                }

                Take();
                return token.Text switch
                {
                    "None" => new ConstExpr(PyNone.Value, token.Line),
                    "True" => new ConstExpr(true, token.Line),
                    "False" => new ConstExpr(false, token.Line),
                    _ => new NameExpr(token.Text, token.Line),
                };
        }

        if (TryTake("("))
        {
            if (TryTake(")"))
            {
                return new TupleExpr(Array.Empty<Expr>(), token.Line);
            }

            var inner = TestList();
            Expect(")");
            return inner;
        }

        if (TryTake("["))
        {
            var items = new List<Expr>();
            while (!Peek.Is("]"))
            {
                items.Add(Test());
                if (!TryTake(","))
                {
                    break;
                }
            }

            Expect("]");
            return new ListExpr(items, token.Line);
        }

        if (TryTake("{"))
        {
            var items = new List<(Expr, Expr)>();
            while (!Peek.Is("}"))
            {
                var key = Test();
                Expect(":");
                items.Add((key, Test()));
                if (!TryTake(","))
                {
                    break;
                }
            }

            Expect("}");
            return new DictExpr(items, token.Line);
        }

        throw Error($"unexpected {token}");
    }
}
