using System.Reflection;

namespace CommunityToolkit.HighPerformance.Conformance;

/// <summary>
/// The expression language an annotation's predicate is written in, parsed once and evaluated by
/// reflection against whichever object the shape offers as a receiver.
///
/// <para>fmverify supplies this whole — there is nothing to fill in here.</para>
///
/// <para><b>An identifier resolves against the receiver, not against the class the annotation was
/// written on.</b> That is the thing to know when a predicate fails with a name it cannot read. The
/// annotation belongs to the type that declares the seam; the object it is read on is whichever one
/// the shape supplies — the state before or after a step, the driver a run was performed with, the
/// result it returned, or an event it emitted. <c>ADOPTION.md</c> carries the table.</para>
///
/// <para><b>A name this cannot read is a failure, never <c>false</c>.</b> An annotation the checker
/// could not evaluate has not been satisfied, and reporting it as satisfied would make the
/// annotation decoration. Every path out of <see cref="Evaluate"/> that did not produce a value
/// carries a reason instead.</para>
///
/// <para>A <c>nameof</c> annotation is not a special case: <c>[Requires(nameof(CanPlace))]</c>
/// arrives here as the one-identifier expression <c>CanPlace</c> and resolves to the same
/// parameterless <c>bool</c> method it always did. There is no second code path for it.</para>
/// </summary>
public sealed class PredicateExpression
{
    private readonly Node _root;

    private PredicateExpression(Node root, bool usesOld)
    {
        _root = root;
        UsesOld = usesOld;
    }

    /// <summary>
    /// Whether the expression names the entry state with <c>old(...)</c>. Known from the parse, so
    /// a caller can refuse the expression on a shape that has no entry object to bind rather than
    /// discovering it once per observed state.
    /// </summary>
    public bool UsesOld { get; }

    /// <summary>
    /// Parses <paramref name="text"/>. On failure <paramref name="error"/> names what was wrong and
    /// where, because an adopter's first expression is usually their first parse error.
    /// </summary>
    public static bool TryParse(string text, out PredicateExpression? expression, out string? error)
    {
        try
        {
            var tokens = Tokenize(text);
            var parser = new Parser(tokens);
            var root = parser.ParseAll();

            expression = new PredicateExpression(root, UsesOldIn(root));
            error = null;
            return true;
        }
        catch (PredicateSyntaxException ex)
        {
            expression = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Evaluates against <paramref name="receiver"/>, with <paramref name="before"/> bound to
    /// <c>old(...)</c>. Pass null for <paramref name="before"/> where the shape has no entry object:
    /// an expression that then reaches for <c>old(...)</c> fails rather than silently reading the
    /// after-value.
    /// </summary>
    public EvaluationOutcome Evaluate(object receiver, object? before) =>
        Evaluate(_root, receiver, before);

    private static bool UsesOldIn(Node node) => node switch
    {
        Node.Old => true,
        Node.Not n => UsesOldIn(n.Operand),
        Node.Binary b => UsesOldIn(b.Left) || UsesOldIn(b.Right),

        // A quantifier's body cannot contain one — the parser refuses it — so there is nothing to
        // recurse into. Listed rather than left to the default arm so a reader sees the decision.
        Node.Quantifier => false,
        _ => false,
    };

    private static EvaluationOutcome Evaluate(Node node, object receiver, object? before) => node switch
    {
        Node.IntLiteral i => new EvaluationOutcome(i.Value, null),
        Node.BoolLiteral b => new EvaluationOutcome(b.Value, null),
        Node.Member m => ReadPath(receiver, m.Path, m.Shown),
        Node.Old o => before is null
            ? new EvaluationOutcome(null,
                $"old({o.Name}) needs the state the seam was entered in, and none was supplied.")
            : Read(before, o.Name, $"old({o.Name})"),
        Node.Not n => EvaluateNot(n, receiver, before),
        Node.Binary b => EvaluateBinary(b, receiver, before),
        Node.Quantifier q => EvaluateQuantifier(q, receiver),

        // Every node the parser can build is handled above. The arm exists because the compiler
        // cannot prove that of a record hierarchy, not because there is a node kind left over.
        _ => throw new InvalidOperationException($"unhandled expression node '{node.GetType().Name}'."),
    };

    private static EvaluationOutcome EvaluateNot(Node.Not node, object receiver, object? before)
    {
        var operand = Evaluate(node.Operand, receiver, before);
        if (operand.Failure is not null) return operand;
        if (operand.Value is not bool b)
            return new EvaluationOutcome(null, $"'!' needs a bool, and got {Describe(operand.Value)}.");

        return new EvaluationOutcome(!b, null);
    }

    /// <summary>
    /// Every element, or one of them, with the element as the receiver.
    ///
    /// <para><b>An empty source is true for <c>all</c> and flagged vacuous.</b> True because failing
    /// on it is unusable — the grammar has no short-circuit, so <c>Count == 0 || all(...)</c> would
    /// still evaluate the quantifier and still fail, leaving an adopter with an empty collection no
    /// way to write the claim at all. Flagged because a green predicate that checked nothing is the
    /// false green this layer exists to prevent, and the checker reports an annotation that was
    /// vacuous on every evaluation.</para>
    ///
    /// <para><c>any</c> over an empty source is false, which needs no flag: a predicate that failed
    /// is not a predicate anybody mistakes for evidence.</para>
    ///
    /// <para>No <paramref name="before"/> is threaded, because the parser refuses <c>old(...)</c>
    /// inside a body. A body that could reach an entry state would need a correspondence between it
    /// and an element that nothing here has.</para>
    /// </summary>
    private static EvaluationOutcome EvaluateQuantifier(Node.Quantifier node, object receiver)
    {
        var shown = node.RequiresAll ? "all" : "any";
        var (source, failure) = WalkTo(receiver, node.Source);
        if (failure is not null) return new EvaluationOutcome(null, failure);

        if (source is not System.Collections.IEnumerable elements || source is string)
            return new EvaluationOutcome(null,
                $"{shown}({node.Source.Shown}, ...) needs a collection, and '{node.Source.Shown}' is " +
                $"{Describe(source)}.");

        var seen = 0;
        var at = 0;
        foreach (var element in elements)
        {
            if (element is null)
                return new EvaluationOutcome(null,
                    $"element {at} of '{node.Source.Shown}' is null, so the body cannot be read on it.");

            var outcome = Evaluate(node.Body, element, before: null);
            if (outcome.Failure is not null)
                return new EvaluationOutcome(null,
                    $"in {shown}({node.Source.Shown}, ...) at element {at}: {outcome.Failure}");

            if (outcome.Value is not bool held)
                return new EvaluationOutcome(null,
                    $"the body of {shown}({node.Source.Shown}, ...) must be a bool, and at element " +
                    $"{at} it was {Describe(outcome.Value)}.");

            seen++;
            at++;

            if (node.RequiresAll && !held) return new EvaluationOutcome(false, null);
            if (!node.RequiresAll && held) return new EvaluationOutcome(true, null);
        }

        return node.RequiresAll
            ? new EvaluationOutcome(true, null, Vacuous: seen == 0)
            : new EvaluationOutcome(false, null);
    }

    /// <summary>Walks every segment of a path, returning the object the last one produced.</summary>
    private static (object? Value, string? Failure) WalkTo(object receiver, Node.Member member)
    {
        object? current = receiver;

        foreach (var segment in member.Path)
        {
            if (current is null)
                return (null, $"a segment of '{member.Shown}' is null, so the rest cannot be read.");

            var (value, failure) = ReadRaw(current, segment, $"{segment}' in '{member.Shown}");
            if (failure is not null) return (null, failure);
            current = value;
        }

        return (current, null);
    }

    private static EvaluationOutcome EvaluateBinary(Node.Binary node, object receiver, object? before)
    {
        // Both sides always, with no short-circuit. `false && anything` is false in C#, but a
        // predicate whose other side names nothing must not pass because this one settled it: that
        // would report an annotation nobody could read as one the code satisfied. The grammar has no
        // indexing and no method arguments, so the null-guarding idiom short-circuiting exists for
        // cannot be written here anyway, and nothing is lost by evaluating both.
        var left = Evaluate(node.Left, receiver, before);
        if (left.Failure is not null) return left;
        var right = Evaluate(node.Right, receiver, before);
        if (right.Failure is not null) return right;

        return node.Op switch
        {
            "&&" or "||" => Logical(node.Op, left.Value, right.Value),
            "+" or "-" => Additive(node.Op, left.Value, right.Value),
            "==" or "!=" => Equality(node.Op, left.Value, right.Value),
            _ => Relational(node.Op, left.Value, right.Value),
        };
    }

    private static EvaluationOutcome Logical(string op, object? left, object? right)
    {
        if (left is not bool l || right is not bool r)
            return new EvaluationOutcome(null,
                $"'{op}' needs two bools, and got {Describe(left)} and {Describe(right)}.");

        return new EvaluationOutcome(op == "&&" ? l && r : l || r, null);
    }

    private static EvaluationOutcome Additive(string op, object? left, object? right)
    {
        if (left is not int l || right is not int r)
            return new EvaluationOutcome(null,
                $"'{op}' needs two ints, and got {Describe(left)} and {Describe(right)}.");

        return new EvaluationOutcome(op == "+" ? l + r : l - r, null);
    }

    private static EvaluationOutcome Equality(string op, object? left, object? right)
    {
        var equal = (left, right) switch
        {
            (int l, int r) => l == r,
            (bool l, bool r) => l == r,
            _ => (bool?)null,
        };

        if (equal is null)
            return new EvaluationOutcome(null,
                $"'{op}' compares two ints or two bools, and got {Describe(left)} and " +
                $"{Describe(right)}.");

        return new EvaluationOutcome(op == "==" ? equal.Value : !equal.Value, null);
    }

    private static EvaluationOutcome Relational(string op, object? left, object? right)
    {
        if (left is not int l || right is not int r)
            return new EvaluationOutcome(null,
                $"'{op}' needs two ints, and got {Describe(left)} and {Describe(right)}.");

        var holds = op switch
        {
            "<" => l < r,
            "<=" => l <= r,
            ">" => l > r,
            _ => l >= r,
        };

        return new EvaluationOutcome(holds, null);
    }

    /// <summary>
    /// Walks a dotted path, stopping at the first segment that cannot be read.
    ///
    /// <para>Only the last segment has to be a bool or an int; the ones before it are the objects
    /// the path travels through, so they are read without that constraint. A path through a null is
    /// a failure naming the segment, never <c>false</c> — the whole point of this file's rule that an
    /// annotation the checker could not evaluate has not been satisfied.</para>
    /// </summary>
    private static EvaluationOutcome ReadPath(object target, IReadOnlyList<string> path, string shownAs)
    {
        // A path whose first segment is not a member of the receiver may be naming an enum member —
        // `TaskState.Done`. Tried second, never first: a member the receiver actually has wins,
        // because it is the receiver's own API and an enum that shadowed it would be a surprise
        // nobody could see from the annotation.
        if (path.Count >= 2 && !HasMember(target, path[0]))
            return ReadEnumMember(target, path, shownAs);

        var current = target;

        for (var at = 0; at < path.Count - 1; at++)
        {
            var (value, failure) = ReadRaw(current, path[at], $"{path[at]}' in '{shownAs}");
            if (failure is not null) return new EvaluationOutcome(null, failure);

            if (value is null)
                return new EvaluationOutcome(null,
                    $"'{path[at]}' in '{shownAs}' is null, so the rest of the path cannot be read.");

            current = value;
        }

        return Read(current, path[^1], shownAs);
    }

    private static bool HasMember(object target, string name)
    {
        var type = target.GetType();
        return type.GetProperty(name)?.GetMethod is not null || type.GetMethod(name, Type.EmptyTypes) is not null;
    }

    /// <summary>
    /// A path read as an enum member — <c>TaskState.Done</c> — and returned as its underlying int.
    ///
    /// <para><b>Why this exists.</b> The expression language reads <c>bool</c> and <c>int</c>, and a
    /// state machine's state is neither: it is an enum. Without this the only way to write a claim
    /// about one was to compare against the number — <c>State == 2</c> — which delivers the claim and
    /// puts a magic number into something a human reads. That was the last thing standing between the
    /// grammar and the claim the TaskFlow round could not write.</para>
    ///
    /// <para><b>The enum is looked for in the receiver's own assembly</b>, and only there. Searching
    /// everything loaded would make the meaning of an annotation depend on what else happened to be
    /// in the process, and a name that resolved differently in the gate than in a unit test is worse
    /// than a name that does not resolve. Two enums of the same name are refused with both named
    /// rather than picked between — I-NO-GUESS, in the one place this file can reach it.</para>
    /// </summary>
    private static EvaluationOutcome ReadEnumMember(
        object target, IReadOnlyList<string> path, string shownAs)
    {
        var typeName = string.Join(".", path.Take(path.Count - 1));
        var memberName = path[^1];
        var assembly = target.GetType().Assembly;

        Type[] candidates;
        try
        {
            candidates = [.. assembly.GetTypes()
                .Where(t => t.IsEnum && (t.Name == typeName || t.FullName == typeName))];
        }
        catch (ReflectionTypeLoadException ex)
        {
            candidates = [.. ex.Types.Where(t => t is { IsEnum: true })
                .Select(t => t!)
                .Where(t => t.Name == typeName || t.FullName == typeName)];
        }

        if (candidates.Length == 0)
            return new EvaluationOutcome(null,
                $"'{shownAs}' is not a member of {target.GetType().Name}, and '{typeName}' is not an " +
                $"enum in {assembly.GetName().Name}.");

        if (candidates.Length > 1)
            return new EvaluationOutcome(null,
                $"'{typeName}' in '{shownAs}' is ambiguous: {assembly.GetName().Name} has " +
                string.Join(" and ", candidates.Select(c => c.FullName)) +
                ". Write the full name.");

        var enumType = candidates[0];
        if (!Enum.TryParse(enumType, memberName, ignoreCase: false, out var parsed) || parsed is null)
            return new EvaluationOutcome(null,
                $"'{memberName}' in '{shownAs}' is not a member of {enumType.Name}. It has " +
                string.Join(", ", Enum.GetNames(enumType)) + ".");

        return AsInt(parsed, shownAs, enumType.Name);
    }

    /// <summary>
    /// An enum as its underlying integer, refused where it does not fit one.
    ///
    /// <para>A long-backed enum outside <c>int</c>'s range would silently wrap through
    /// <c>Convert.ToInt32</c>, and a comparison against a wrapped value is a wrong answer rather than
    /// a missing one.</para>
    /// </summary>
    private static EvaluationOutcome AsInt(object value, string shownAs, string typeName)
    {
        try
        {
            return new EvaluationOutcome(Convert.ToInt32(value), null);
        }
        catch (OverflowException)
        {
            return new EvaluationOutcome(null,
                $"'{shownAs}' is {typeName}.{value}, whose value does not fit in an int.");
        }
    }

    /// <summary>
    /// Resolves one name on one object with no constraint on the result's type: a public property
    /// with a getter, else a public parameterless method.
    ///
    /// <para>Split out of <see cref="Read"/> when paths arrived. A segment in the middle of a path
    /// and the source of a quantifier are both objects rather than values, so the bool-or-int rule
    /// belongs to the leaf and not to the lookup.</para>
    /// </summary>
    private static (object? Value, string? Failure) ReadRaw(object target, string name, string shownAs)
    {
        var type = target.GetType();

        try
        {
            var property = type.GetProperty(name);
            if (property?.GetMethod is not null) return (property.GetValue(target), null);

            var method = type.GetMethod(name, Type.EmptyTypes);
            if (method is null)
                return (null, $"'{shownAs}' is not a public property or parameterless method on {type.Name}.");

            return (method.Invoke(target, null), null);
        }
        catch (TargetInvocationException ex)
        {
            // Unwrapped: the reflection wrapper's own name tells a reader nothing, and the thing
            // that actually threw is inside it.
            var cause = ex.InnerException ?? ex;
            return (null, $"'{shownAs}' on {type.Name} threw {cause.GetType().Name}: {cause.Message}");
        }
        catch (Exception ex)
        {
            return (null, $"'{shownAs}' on {type.Name} threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves one name on one object and requires the result to be a bool or an int. The same
    /// binding G1 used, so an annotation that resolved before resolves now.
    /// </summary>
    private static EvaluationOutcome Read(object target, string name, string shownAs)
    {
        var (value, failure) = ReadRaw(target, name, shownAs);
        if (failure is not null) return new EvaluationOutcome(null, failure);
        if (value is bool or int) return new EvaluationOutcome(value, null);

        // An enum-valued member reads as its underlying int, so `State == TaskState.Done` compares
        // two ints and `State` alone is usable in arithmetic. Only at the leaf: a segment a path
        // travels through is an object, and an enum cannot be one.
        if (value is Enum) return AsInt(value, shownAs, value.GetType().Name);

        return new EvaluationOutcome(null,
            $"'{shownAs}' on {target.GetType().Name} is {Describe(value)}; an expression can only " +
            "read bool or int.");
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        bool b => $"the bool {b}",
        int i => $"the int {i}",
        _ => $"a {value.GetType().Name}",
    };

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text[start..i];
                tokens.Add(new Token(word switch
                {
                    "true" => Kind.True,
                    "false" => Kind.False,
                    "old" => Kind.Old,
                    "all" => Kind.All,
                    "any" => Kind.Any,
                    _ => Kind.Identifier,
                }, word, start));
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && char.IsDigit(text[i])) i++;
                var digits = text[start..i];
                if (!int.TryParse(digits, out _))
                    throw new PredicateSyntaxException($"'{digits}' is not an int, at {start}.");

                tokens.Add(new Token(Kind.Number, digits, start));
                continue;
            }

            var two = i + 1 < text.Length ? text.Substring(i, 2) : null;
            switch (two)
            {
                case "&&": tokens.Add(new Token(Kind.AndAnd, two, i)); i += 2; continue;
                case "||": tokens.Add(new Token(Kind.OrOr, two, i)); i += 2; continue;
                case "==": tokens.Add(new Token(Kind.EqEq, two, i)); i += 2; continue;
                case "!=": tokens.Add(new Token(Kind.BangEq, two, i)); i += 2; continue;
                case "<=": tokens.Add(new Token(Kind.Le, two, i)); i += 2; continue;
                case ">=": tokens.Add(new Token(Kind.Ge, two, i)); i += 2; continue;
            }

            Kind? single = c switch
            {
                '(' => Kind.LParen,
                ')' => Kind.RParen,
                '!' => Kind.Bang,
                '<' => Kind.Lt,
                '>' => Kind.Gt,
                '+' => Kind.Plus,
                '-' => Kind.Minus,
                '.' => Kind.Dot,
                ',' => Kind.Comma,
                _ => null,
            };

            // A lone '&', '|' or '=' lands here too, which is the right answer: the language has no
            // bitwise or assignment operator, so half of one is a typo rather than a shorter form.
            if (single is null)
                throw new PredicateSyntaxException($"'{c}' is not part of the expression language, at {i}.");

            tokens.Add(new Token(single.Value, c.ToString(), i));
            i++;
        }

        tokens.Add(new Token(Kind.End, "", text.Length));
        return tokens;
    }

    private enum Kind
    {
        Identifier, Number, True, False, Old,
        LParen, RParen, Bang,
        AndAnd, OrOr,
        Lt, Le, Gt, Ge, EqEq, BangEq,
        Plus, Minus, Dot, Comma,
        All, Any,
        End,
    }

    private readonly record struct Token(Kind Kind, string Text, int Position);

    private sealed class PredicateSyntaxException(string message) : Exception(message);

    /// <summary>
    /// One node of a parsed expression. Untyped on purpose: a bare identifier's type is not known
    /// until it is read off a receiver, so the kinds are checked during evaluation and a mismatch is
    /// a named failure rather than a parse error.
    /// </summary>
    private abstract record Node
    {
        public sealed record IntLiteral(int Value) : Node;

        public sealed record BoolLiteral(bool Value) : Node;

        /// <summary>
        /// A dotted path read left to right off the current receiver: <c>Final</c> is one segment,
        /// <c>Final.Count</c> is two. Only the last segment has to be a bool or an int — the ones
        /// before it are the objects the path travels through.
        /// </summary>
        public sealed record Member(IReadOnlyList<string> Path) : Node
        {
            public string Shown => string.Join(".", Path);
        }

        public sealed record Old(string Name) : Node;

        public sealed record Not(Node Operand) : Node;

        public sealed record Binary(string Op, Node Left, Node Right) : Node;

        /// <summary>
        /// <c>all(Source, Body)</c> or <c>any(Source, Body)</c>.
        ///
        /// <para><b>The element is the receiver inside the body</b>, so there is no binder to name.
        /// That is the grammar's existing rule stated once more — a bare identifier resolves against
        /// the current receiver — and a quantifier changes the current receiver to the element. The
        /// cost is that a body cannot reach the outer receiver, which is paid where the claim is
        /// written: <c>!RanToCompletion || all(Final.Values, State == 2)</c> puts the outer part
        /// outside.</para>
        /// </summary>
        public sealed record Quantifier(bool RequiresAll, Node.Member Source, Node Body) : Node;
    }

    /// <summary>
    /// Recursive descent, loosest binding outwards: <c>||</c>, then <c>&amp;&amp;</c>, then one
    /// comparison, then <c>+</c>/<c>-</c>, then unary <c>!</c>, then a primary. The comparison level
    /// is deliberately non-associative — <c>a &lt; b &lt; c</c> is a mistake, not an expression, and
    /// C# rejects it too.
    /// </summary>
    private sealed class Parser(List<Token> tokens)
    {
        private int _at;

        public Node ParseAll()
        {
            var node = ParseOr();
            if (Current.Kind != Kind.End)
                throw new PredicateSyntaxException(
                    $"unexpected '{Current.Text}' at {Current.Position}, after the expression ended.");

            return node;
        }

        private Token Current => tokens[_at];

        private Node ParseOr()
        {
            var node = ParseAnd();
            while (Current.Kind == Kind.OrOr)
            {
                _at++;
                node = new Node.Binary("||", node, ParseAnd());
            }

            return node;
        }

        private Node ParseAnd()
        {
            var node = ParseComparison();
            while (Current.Kind == Kind.AndAnd)
            {
                _at++;
                node = new Node.Binary("&&", node, ParseComparison());
            }

            return node;
        }

        private Node ParseComparison()
        {
            var node = ParseAdditive();
            var op = Current.Kind switch
            {
                Kind.Lt => "<",
                Kind.Le => "<=",
                Kind.Gt => ">",
                Kind.Ge => ">=",
                Kind.EqEq => "==",
                Kind.BangEq => "!=",
                _ => null,
            };

            if (op is null) return node;

            _at++;
            return new Node.Binary(op, node, ParseAdditive());
        }

        private Node ParseAdditive()
        {
            var node = ParseUnary();
            while (Current.Kind is Kind.Plus or Kind.Minus)
            {
                var op = Current.Kind == Kind.Plus ? "+" : "-";
                _at++;
                node = new Node.Binary(op, node, ParseUnary());
            }

            return node;
        }

        private Node ParseUnary()
        {
            if (Current.Kind != Kind.Bang) return ParsePrimary();

            _at++;
            return new Node.Not(ParseUnary());
        }

        private Node ParsePrimary()
        {
            var token = Current;
            switch (token.Kind)
            {
                case Kind.Number:
                    _at++;
                    return new Node.IntLiteral(int.Parse(token.Text));

                case Kind.True:
                case Kind.False:
                    _at++;
                    return new Node.BoolLiteral(token.Kind == Kind.True);

                case Kind.Identifier:
                    return new Node.Member(ParsePath());

                case Kind.Old:
                    _at++;
                    Expect(Kind.LParen, "(");
                    var name = Current;
                    if (name.Kind != Kind.Identifier)
                        throw new PredicateSyntaxException(
                            $"old(...) takes one member name, and got '{name.Text}' at {name.Position}.");
                    _at++;
                    Expect(Kind.RParen, ")");
                    return new Node.Old(name.Text);

                case Kind.All:
                case Kind.Any:
                {
                    var requiresAll = token.Kind == Kind.All;
                    var shown = requiresAll ? "all" : "any";
                    _at++;
                    Expect(Kind.LParen, "(");

                    if (Current.Kind != Kind.Identifier)
                        throw new PredicateSyntaxException(
                            $"{shown}(...) takes a member path as its first argument, and got " +
                            $"'{Current.Text}' at {Current.Position}.");

                    var source = new Node.Member(ParsePath());
                    Expect(Kind.Comma, ",");
                    var body = ParseOr();
                    Expect(Kind.RParen, ")");

                    // Refused here rather than at evaluation, so the message arrives once at parse
                    // time instead of per observed state. On the step shape old(x) names the same
                    // object's earlier value; between an entry state and an element of a collection
                    // the run produced there is no such correspondence, and inventing one would make
                    // the notation mean two things.
                    if (UsesOldIn(body))
                        throw new PredicateSyntaxException(
                            $"old(...) cannot be used inside {shown}(...): there is no earlier value " +
                            "of an element the run produced for it to name. Move it outside the " +
                            "quantifier.");

                    return new Node.Quantifier(requiresAll, source, body);
                }

                case Kind.LParen:
                    _at++;
                    var inner = ParseOr();
                    Expect(Kind.RParen, ")");
                    return inner;

                case Kind.End:
                    throw new PredicateSyntaxException(
                        $"the expression ended at {token.Position}, where a value was expected.");

                default:
                    throw new PredicateSyntaxException(
                        $"unexpected '{token.Text}' at {token.Position}, where a value was expected.");
            }
        }

        /// <summary>
        /// An identifier and every <c>.</c>-separated segment after it. A trailing dot is a syntax
        /// error rather than a one-segment path, because it is a typo every time.
        /// </summary>
        private List<string> ParsePath()
        {
            var path = new List<string> { Current.Text };
            _at++;

            while (Current.Kind == Kind.Dot)
            {
                _at++;
                if (Current.Kind != Kind.Identifier)
                    throw new PredicateSyntaxException(
                        $"a '.' at {Current.Position - 1} must be followed by a member name, and got " +
                        (Current.Kind == Kind.End ? "the end of the expression." : $"'{Current.Text}'."));

                path.Add(Current.Text);
                _at++;
            }

            return path;
        }

        private void Expect(Kind kind, string shown)
        {
            if (Current.Kind != kind)
                throw new PredicateSyntaxException(
                    $"expected '{shown}' at {Current.Position}, and found " +
                    (Current.Kind == Kind.End ? "the end of the expression." : $"'{Current.Text}'."));

            _at++;
        }
    }
}

/// <summary>
/// What one evaluation produced: a boxed <c>bool</c> or <c>int</c> in <paramref name="Value"/>, or a
/// reason in <paramref name="Failure"/>. Exactly one of the two is null.
///
/// <para>The caller says which type it wanted by checking <paramref name="Value"/> — a contract
/// annotation wants a bool, a <c>[Measure]</c> wants an int — so a mismatch reads as the failure it
/// is rather than throwing a cast.</para>
/// </summary>
/// <param name="Vacuous">
/// That this evaluation checked nothing: an <c>all(...)</c> whose collection was empty, which is
/// <c>true</c> without having looked at anything.
///
/// <para>True rather than a failure because failing would be unusable — the grammar has no
/// short-circuit, so <c>Count == 0 || all(...)</c> would still evaluate the quantifier. Flagged
/// because a green predicate that checked nothing is exactly the false green this layer exists to
/// prevent, and the checker turns an annotation that was vacuous on <em>every</em> evaluation into a
/// failure. One vacuous evaluation is legitimate; all of them means the annotation is decoration.
/// Trailing and defaulted because this record is public contract.</para>
/// </param>
public sealed record EvaluationOutcome(object? Value, string? Failure, bool Vacuous = false);
