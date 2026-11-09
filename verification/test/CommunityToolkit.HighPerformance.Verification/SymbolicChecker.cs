using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Z3;

namespace CommunityToolkit.HighPerformance.Verification;

public abstract record SymbolicResult
{
    public sealed record Divergence(IReadOnlyDictionary<string, int> Counterexample) : SymbolicResult;
    public sealed record NoDivergence : SymbolicResult;
    public sealed record Unsupported(string Reason) : SymbolicResult;
}

public abstract record SymbolicCheckResult
{
    public sealed record Verified(string MethodName) : SymbolicCheckResult;

    public sealed record ContractViolation(
        string MethodName,
        string ViolatedEnsures,
        IReadOnlyDictionary<string, object> Inputs,
        object ActualResult) : SymbolicCheckResult;

    public sealed record OverflowDivergence(
        string MethodName,
        IReadOnlyDictionary<string, object> Inputs,
        int RealResult,
        BigInteger IdealResult) : SymbolicCheckResult;

    public sealed record PreconditionContradiction(string MethodName, string Precondition) : SymbolicCheckResult;

    public sealed record Unsupported(string MethodName, string Reason) : SymbolicCheckResult;
}

public sealed record LocatedMethod(
    string Owner,
    string Signature,
    string File,
    int Line,
    MethodDeclarationSyntax Method);

public sealed record LocatedSymbolicResult(LocatedMethod Location, SymbolicCheckResult Result);

public sealed record MethodContracts(IReadOnlyList<string> Requires, IReadOnlyList<string> Ensures)
{
    public string? ConjoinedRequires => Requires.Count switch
    {
        0 => null,
        1 => Requires[0],
        _ => string.Join(" && ", Requires.Select(r => $"({r})")),
    };
}

public static class ContractExtractor
{
    public static MethodContracts Extract(string source, string methodName)
    {
        var method = MethodSource.Read(source, methodName);
        return Extract(method);
    }

    public static MethodContracts Extract(MethodDeclarationSyntax method)
    {
        var requires = new List<string>();
        var ensures = new List<string>();

        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (IsMatchingAttribute(name, "Requires"))
                {
                    if (ExtractStringArgument(attr) is { } reqExpr)
                        requires.Add(reqExpr);
                }
                else if (IsMatchingAttribute(name, "Ensures"))
                {
                    if (ExtractStringArgument(attr) is { } ensExpr)
                        ensures.Add(ensExpr);
                }
            }
        }

        return new MethodContracts(requires, ensures);
    }

    private static bool IsMatchingAttribute(string actualName, string targetAttribute) =>
        actualName == targetAttribute
        || actualName == targetAttribute + "Attribute"
        || actualName.EndsWith("." + targetAttribute, StringComparison.Ordinal)
        || actualName.EndsWith("." + targetAttribute + "Attribute", StringComparison.Ordinal);

    private static string? ExtractStringArgument(AttributeSyntax attr)
    {
        var arg = attr.ArgumentList?.Arguments.FirstOrDefault();
        if (arg?.Expression is LiteralExpressionSyntax { Token.Value: string strVal })
            return strVal;
        return null;
    }
}

public static class MethodSource
{
    public static MethodDeclarationSyntax Read(string source, string methodName)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var matches = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"no method named '{methodName}' in the source."),
            _ => throw new InvalidOperationException(
                $"'{methodName}' is declared {matches.Count} times; the name must be unique."),
        };
    }
}

/// <summary>
/// The two C# int bounds accepted in annotation expressions. The annotation language is a
/// predicate grammar, not compiled C#, so the symbolic engine resolves these itself.
/// </summary>
internal static class IntBounds
{
    public static bool TryGet(MemberAccessExpressionSyntax member, out int value)
    {
        if (member.Expression is PredefinedTypeSyntax predefined &&
            predefined.Keyword.Text == "int" &&
            member.Name.Identifier.Text is "MaxValue" or "MinValue")
        {
            value = member.Name.Identifier.Text == "MaxValue"
                ? int.MaxValue
                : int.MinValue;
            return true;
        }

        value = 0;
        return false;
    }
}

public static class SubsetValidator
{
    private static readonly int IdealMagnitudeBits = (int)DualEncoder.IdealWidth - 1;

    public static string? Validate(MethodDeclarationSyntax method)
    {
        foreach (var p in method.ParameterList.Parameters)
        {
            if (p.Modifiers.Any())
                return $"parameter '{p.Identifier.Text}' has modifier '{p.Modifiers[0].Text}'";
            if (p.Type is not PredefinedTypeSyntax pt || !pt.Keyword.IsKind(SyntaxKind.IntKeyword))
                return $"parameter '{p.Identifier.Text}' is '{p.Type}', not int";
        }

        if (method.ExpressionBody is { } arrow)
            return ValidateExpression(arrow.Expression);

        if (method.Body is { } block)
        {
            foreach (var stmt in block.Statements)
            {
                switch (stmt)
                {
                    case IfStatementSyntax ifs:
                        if (ifs.Else is not null)
                            return "if-with-else is not supported; use guarded returns then a final return";
                        if (ifs.Statement is not ReturnStatementSyntax { Expression: { } } gret)
                            return "an if body must be a single 'return <expr>;'";
                        {
                            var c = ValidateExpression(ifs.Condition);
                            if (c is not null) return c;
                            var r = ValidateExpression(gret.Expression);
                            if (r is not null) return r;
                        }
                        break;
                    case ReturnStatementSyntax { Expression: { } re }:
                        var er = ValidateExpression(re);
                        if (er is not null) return er;
                        break;
                    default:
                        return $"statement '{stmt.Kind()}' is not supported";
                }
            }

            if (block.Statements.Count == 0 || block.Statements[^1] is not ReturnStatementSyntax)
                return "block must end with an unconditional return";

            return null;
        }

        return "method has no body";
    }

    private static string? ValidateExpression(ExpressionSyntax expr)
    {
        foreach (var node in expr.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ParenthesizedExpressionSyntax:
                case IdentifierNameSyntax:
                case ConditionalExpressionSyntax:
                case MemberAccessExpressionSyntax member when IntBounds.TryGet(member, out _):
                    break;
                case PredefinedTypeSyntax predefined when
                    predefined.Parent is MemberAccessExpressionSyntax parent &&
                    IntBounds.TryGet(parent, out _):
                    break;
                case LiteralExpressionSyntax lit when lit.Token.Value is int or bool:
                    break;
                case LiteralExpressionSyntax lit:
                    return $"literal '{lit.Token.Text}' is neither int nor bool";
                case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                    break;
                case BinaryExpressionSyntax b when IsSupportedBinary(b.Kind()):
                    break;
                case BinaryExpressionSyntax b:
                    return $"operator '{b.OperatorToken.Text}'";
                case InvocationExpressionSyntax:
                    return "method calls are not supported";
                default:
                    return $"expression '{node.Kind()}'";
            }
        }

        foreach (var node in expr.DescendantNodesAndSelf().OfType<BinaryExpressionSyntax>())
        {
            if (MagnitudeBits(node) is int bits && bits > IdealMagnitudeBits)
                return $"'{node}' can reach 2^{bits}, beyond the ideal interpretation's " +
                       $"{DualEncoder.IdealWidth}-bit headroom (2^{IdealMagnitudeBits})";
        }

        return null;
    }

    private static int? MagnitudeBits(ExpressionSyntax expr) => expr switch
    {
        ParenthesizedExpressionSyntax p => MagnitudeBits(p.Expression),
        LiteralExpressionSyntax { Token.Value: int v } => LiteralMagnitudeBits(v),
        MemberAccessExpressionSyntax member when IntBounds.TryGet(member, out var bound) =>
            LiteralMagnitudeBits(bound),
        // Any Int32, so at most 2^31 — which int.MinValue reaches exactly.
        IdentifierNameSyntax => 31,
        ConditionalExpressionSyntax c => ConditionalMagnitudeBits(c),
        BinaryExpressionSyntax b => BinaryMagnitudeBits(b),
        _ => null,
    };

    private static int? ConditionalMagnitudeBits(ConditionalExpressionSyntax c) =>
        MagnitudeBits(c.WhenTrue) is int t && MagnitudeBits(c.WhenFalse) is int f
            ? Math.Max(t, f)
            : null;

    private static int? BinaryMagnitudeBits(BinaryExpressionSyntax b)
    {
        if (!IsArithmetic(b.Kind()))
            return null;
        if (MagnitudeBits(b.Left) is not int left || MagnitudeBits(b.Right) is not int right)
            return null;

        return b.IsKind(SyntaxKind.MultiplyExpression) ? left + right : Math.Max(left, right) + 1;
    }

    private static int LiteralMagnitudeBits(int v)
    {
        var magnitude = Math.Abs((long)v);
        var bits = 0;
        while (1L << bits < magnitude)
            bits++;
        return bits;
    }

    private static bool IsArithmetic(SyntaxKind kind) => kind is
        SyntaxKind.AddExpression or SyntaxKind.SubtractExpression or SyntaxKind.MultiplyExpression;

    private static bool IsSupportedBinary(SyntaxKind kind) => IsArithmetic(kind) ||
        kind is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
        SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or
        SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or
        SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression;
}

public abstract record Encoded;
public sealed record IntEncoded(BitVecExpr Bv, BitVecExpr Ideal) : Encoded;
public sealed record BoolEncoded(BoolExpr Bv, BoolExpr Ideal) : Encoded;

public sealed class DualEncoder
{
    public const uint IdealWidth = 128;

    private readonly Context _ctx;
    private readonly IReadOnlyDictionary<string, (BitVecExpr Bv, BitVecExpr Ideal)> _inputs;
    private readonly Encoded? _resultBinding;

    public DualEncoder(
        Context ctx,
        IReadOnlyDictionary<string, (BitVecExpr Bv, BitVecExpr Ideal)> inputs,
        Encoded? resultBinding = null)
    {
        _ctx = ctx;
        _inputs = inputs;
        _resultBinding = resultBinding;
    }

    public Encoded Encode(ExpressionSyntax expr) => expr switch
    {
        ParenthesizedExpressionSyntax p => Encode(p.Expression),
        LiteralExpressionSyntax lit => EncodeLiteral(lit),
        MemberAccessExpressionSyntax member when IntBounds.TryGet(member, out var bound) =>
            EncodeInt(bound),
        IdentifierNameSyntax id => EncodeIdentifier(id),
        PrefixUnaryExpressionSyntax u => EncodeUnary(u),
        BinaryExpressionSyntax b => EncodeBinary(b),
        ConditionalExpressionSyntax c => EncodeConditional(c),
        _ => throw new NotSupportedException($"expression '{expr.Kind()}'"),
    };

    private Encoded EncodeLiteral(LiteralExpressionSyntax lit) => lit.Token.Value switch
    {
        int v => EncodeInt(v),
        // A boolean constant is the same under both interpretations: no width, nothing to wrap.
        bool b => new BoolEncoded(_ctx.MkBool(b), _ctx.MkBool(b)),
        _ => throw new NotSupportedException($"literal '{lit.Token.Text}' is neither int nor bool"),
    };

    /// <summary>
    /// MkBV of a signed int produces its two's-complement value at each width, so the wide
    /// literal is the sign-extension of the 32-bit one: the same integer, with more headroom.
    /// </summary>
    private Encoded EncodeInt(int value) =>
        new IntEncoded(_ctx.MkBV(value, 32), _ctx.MkBV(value, IdealWidth));

    private Encoded EncodeIdentifier(IdentifierNameSyntax id)
    {
        if (id.Identifier.Text == "result")
        {
            if (_resultBinding is not null) return _resultBinding;
            throw new NotSupportedException("identifier 'result' is only supported in [Ensures] postconditions");
        }

        if (!_inputs.TryGetValue(id.Identifier.Text, out var pair))
            throw new NotSupportedException($"identifier '{id.Identifier.Text}' is not an input");
        return new IntEncoded(pair.Bv, pair.Ideal);
    }

    private BoolEncoded EncodeUnary(PrefixUnaryExpressionSyntax u)
    {
        if (u.Kind() != SyntaxKind.LogicalNotExpression)
            throw new NotSupportedException($"unary '{u.OperatorToken.Text}'");
        var (bv, ideal) = Bool(u.Operand);
        return new BoolEncoded((BoolExpr)_ctx.MkNot(bv), (BoolExpr)_ctx.MkNot(ideal));
    }

    private Encoded EncodeBinary(BinaryExpressionSyntax b)
    {
        switch (b.Kind())
        {
            case SyntaxKind.AddExpression:
            case SyntaxKind.SubtractExpression:
            case SyntaxKind.MultiplyExpression:
            {
                var (lbv, lideal) = Int(b.Left);
                var (rbv, rideal) = Int(b.Right);
                return b.Kind() switch
                {
                    SyntaxKind.AddExpression =>
                        new IntEncoded(_ctx.MkBVAdd(lbv, rbv), _ctx.MkBVAdd(lideal, rideal)),
                    SyntaxKind.SubtractExpression =>
                        new IntEncoded(_ctx.MkBVSub(lbv, rbv), _ctx.MkBVSub(lideal, rideal)),
                    _ =>
                        new IntEncoded(_ctx.MkBVMul(lbv, rbv), _ctx.MkBVMul(lideal, rideal)),
                };
            }
            case SyntaxKind.LessThanExpression:
            case SyntaxKind.LessThanOrEqualExpression:
            case SyntaxKind.GreaterThanExpression:
            case SyntaxKind.GreaterThanOrEqualExpression:
            case SyntaxKind.EqualsExpression:
            case SyntaxKind.NotEqualsExpression:
            {
                var (lbv, lideal) = Int(b.Left);
                var (rbv, rideal) = Int(b.Right);
                return b.Kind() switch
                {
                    SyntaxKind.LessThanExpression =>
                        new BoolEncoded(_ctx.MkBVSLT(lbv, rbv), _ctx.MkBVSLT(lideal, rideal)),
                    SyntaxKind.LessThanOrEqualExpression =>
                        new BoolEncoded(_ctx.MkBVSLE(lbv, rbv), _ctx.MkBVSLE(lideal, rideal)),
                    SyntaxKind.GreaterThanExpression =>
                        new BoolEncoded(_ctx.MkBVSGT(lbv, rbv), _ctx.MkBVSGT(lideal, rideal)),
                    SyntaxKind.GreaterThanOrEqualExpression =>
                        new BoolEncoded(_ctx.MkBVSGE(lbv, rbv), _ctx.MkBVSGE(lideal, rideal)),
                    SyntaxKind.EqualsExpression =>
                        new BoolEncoded(_ctx.MkEq(lbv, rbv), _ctx.MkEq(lideal, rideal)),
                    _ =>
                        new BoolEncoded((BoolExpr)_ctx.MkNot(_ctx.MkEq(lbv, rbv)),
                                        (BoolExpr)_ctx.MkNot(_ctx.MkEq(lideal, rideal))),
                };
            }
            case SyntaxKind.LogicalAndExpression:
            case SyntaxKind.LogicalOrExpression:
            {
                var (lbv, lideal) = Bool(b.Left);
                var (rbv, rideal) = Bool(b.Right);
                return b.Kind() == SyntaxKind.LogicalAndExpression
                    ? new BoolEncoded(_ctx.MkAnd(lbv, rbv), _ctx.MkAnd(lideal, rideal))
                    : new BoolEncoded(_ctx.MkOr(lbv, rbv), _ctx.MkOr(lideal, rideal));
            }
            default:
                throw new NotSupportedException($"binary '{b.OperatorToken.Text}'");
        }
    }

    private Encoded EncodeConditional(ConditionalExpressionSyntax c)
    {
        var (cbv, cideal) = Bool(c.Condition);
        var t = Encode(c.WhenTrue);
        var e = Encode(c.WhenFalse);
        return (t, e) switch
        {
            (IntEncoded ti, IntEncoded ei) => new IntEncoded(
                (BitVecExpr)_ctx.MkITE(cbv, ti.Bv, ei.Bv),
                (BitVecExpr)_ctx.MkITE(cideal, ti.Ideal, ei.Ideal)),
            (BoolEncoded tb, BoolEncoded eb) => new BoolEncoded(
                (BoolExpr)_ctx.MkITE(cbv, tb.Bv, eb.Bv),
                (BoolExpr)_ctx.MkITE(cideal, tb.Ideal, eb.Ideal)),
            _ => throw new NotSupportedException("conditional branches differ in type"),
        };
    }

    private (BitVecExpr Bv, BitVecExpr Ideal) Int(ExpressionSyntax e) =>
        Encode(e) is IntEncoded i ? (i.Bv, i.Ideal) : throw new NotSupportedException("expected int");

    private (BoolExpr Bv, BoolExpr Ideal) Bool(ExpressionSyntax e) =>
        Encode(e) is BoolEncoded b ? (b.Bv, b.Ideal) : throw new NotSupportedException("expected bool");
}

public static class SymbolicChecker
{
    private static readonly ConcurrentBag<Context> RetainedContexts = new();

    /// <summary>
    /// Wall-clock cap on one Z3 query. A product of two symbolic operands is a decidable QF_BV
    /// query, but its bit-blasted form can run for hours; without a bound this gate hangs CI
    /// instead of reporting the limit. A query that exhausts this budget returns
    /// <see cref="Status.UNKNOWN"/>, which is reported as Unsupported and never as Verified.
    /// </summary>
    public const uint SolverTimeoutMilliseconds = 5_000;

    private static Solver NewSolver(Context context, uint solverTimeoutMilliseconds)
    {
        var solver = context.MkSolver();
        solver.Set("timeout", solverTimeoutMilliseconds);
        return solver;
    }

    public static SymbolicCheckResult CheckContracts(string source, string methodName) =>
        CheckContracts(source, methodName, SolverTimeoutMilliseconds);

    public static SymbolicCheckResult CheckContracts(
        string source, string methodName, uint solverTimeoutMilliseconds)
    {
        MethodDeclarationSyntax method;
        try
        {
            method = MethodSource.Read(source, methodName);
        }
        catch (InvalidOperationException ex)
        {
            return new SymbolicCheckResult.Unsupported(methodName, ex.Message);
        }

        return CheckContracts(method, solverTimeoutMilliseconds);
    }

    public static SymbolicCheckResult CheckContracts(MethodDeclarationSyntax method) =>
        CheckContracts(method, SolverTimeoutMilliseconds);

    public static SymbolicCheckResult CheckContracts(
        MethodDeclarationSyntax method, uint solverTimeoutMilliseconds)
    {
        var methodName = method.Identifier.Text;
        if (SubsetValidator.Validate(method) is { } reason)
            return new SymbolicCheckResult.Unsupported(methodName, reason);

        var contracts = ContractExtractor.Extract(method);

        var ctx = new Context();
        RetainedContexts.Add(ctx);

        var inputs = new Dictionary<string, (BitVecExpr Bv, BitVecExpr Ideal)>();
        foreach (var p in method.ParameterList.Parameters)
        {
            var bv = ctx.MkBVConst(p.Identifier.Text, 32);
            inputs[p.Identifier.Text] = (bv, ctx.MkSignExt(DualEncoder.IdealWidth - 32, bv));
        }

        var encoder = new DualEncoder(ctx, inputs);

        BoolEncoded? pre = null;
        if (contracts.ConjoinedRequires is { } conjoinedPre)
        {
            try
            {
                var parsedPre = SyntaxFactory.ParseExpression(conjoinedPre);
                if (encoder.Encode(parsedPre) is not BoolEncoded p)
                    return new SymbolicCheckResult.Unsupported(methodName, "precondition is not boolean");
                pre = p;
            }
            catch (NotSupportedException ex)
            {
                return new SymbolicCheckResult.Unsupported(methodName, ex.Message);
            }

            var preSolver = NewSolver(ctx, solverTimeoutMilliseconds);
            preSolver.Assert(pre.Bv);
            var preStatus = preSolver.Check();
            if (preStatus == Status.UNKNOWN)
            {
                return new SymbolicCheckResult.Unsupported(methodName, $"solver returned unknown on precondition: {preSolver.ReasonUnknown} (query budget {solverTimeoutMilliseconds} ms)");
            }
            if (preStatus == Status.UNSATISFIABLE)
            {
                return new SymbolicCheckResult.PreconditionContradiction(methodName, conjoinedPre);
            }
        }

        Encoded body;
        try
        {
            body = EncodeBody(ctx, encoder, method);
        }
        catch (NotSupportedException ex)
        {
            return new SymbolicCheckResult.Unsupported(methodName, ex.Message);
        }

        // Query 1: Arithmetic Overflow Safety
        BoolExpr overflowCond;
        if (body is IntEncoded intBody)
        {
            var signExt = ctx.MkSignExt(DualEncoder.IdealWidth - 32, intBody.Bv);
            overflowCond = (BoolExpr)ctx.MkNot(ctx.MkEq(signExt, intBody.Ideal));
        }
        else if (body is BoolEncoded boolBody)
        {
            overflowCond = ctx.MkXor(boolBody.Bv, boolBody.Ideal);
        }
        else
        {
            return new SymbolicCheckResult.Unsupported(methodName, "unsupported return type");
        }

        var overflowSolver = NewSolver(ctx, solverTimeoutMilliseconds);
        if (pre != null)
        {
            overflowSolver.Assert(pre.Bv);
        }
        overflowSolver.Assert(overflowCond);
        var overflowStatus = overflowSolver.Check();
        if (overflowStatus == Status.UNKNOWN)
        {
            return new SymbolicCheckResult.Unsupported(methodName, $"solver returned unknown on overflow check: {overflowSolver.ReasonUnknown} (query budget {solverTimeoutMilliseconds} ms)");
        }
        if (overflowStatus == Status.SATISFIABLE)
        {
            var model = overflowSolver.Model;
            var counterexample = new Dictionary<string, object>();
            foreach (var (pName, pair) in inputs)
            {
                var bits = ((BitVecNum)model.Eval(pair.Bv, completion: true)).UInt64;
                counterexample[pName] = unchecked((int)(uint)bits);
            }

            if (body is IntEncoded ib)
            {
                var realBits = ((BitVecNum)model.Eval(ib.Bv, completion: true)).UInt64;
                int realResult = unchecked((int)(uint)realBits);
                var idealNum = (BitVecNum)model.Eval(ib.Ideal, completion: true);
                var bi = idealNum.BigInteger;
                var limit = BigInteger.One << 127;
                if (bi >= limit)
                {
                    bi -= BigInteger.One << 128;
                }
                return new SymbolicCheckResult.OverflowDivergence(methodName, counterexample, realResult, bi);
            }
            else
            {
                var realBool = ((BoolExpr)model.Eval(((BoolEncoded)body).Bv, completion: true)).IsTrue;
                return new SymbolicCheckResult.OverflowDivergence(methodName, counterexample, realBool ? 1 : 0, realBool ? 0 : 1);
            }
        }

        // Query 2: Contract Postcondition Correctness
        if (contracts.Ensures.Count > 0)
        {
            var postEncoder = new DualEncoder(ctx, inputs, resultBinding: body);
            foreach (var ensuresClause in contracts.Ensures)
            {
                BoolEncoded postBool;
                try
                {
                    var parsedPost = SyntaxFactory.ParseExpression(ensuresClause);
                    if (postEncoder.Encode(parsedPost) is not BoolEncoded pb)
                        return new SymbolicCheckResult.Unsupported(methodName, $"ensures clause '{ensuresClause}' is not boolean");
                    postBool = pb;
                }
                catch (NotSupportedException ex)
                {
                    return new SymbolicCheckResult.Unsupported(methodName, ex.Message);
                }

                var postSolver = NewSolver(ctx, solverTimeoutMilliseconds);
                if (pre != null)
                {
                    postSolver.Assert(pre.Bv);
                }
                postSolver.Assert(ctx.MkNot(postBool.Bv));
                var postStatus = postSolver.Check();
                if (postStatus == Status.UNKNOWN)
                {
                    return new SymbolicCheckResult.Unsupported(methodName, $"solver returned unknown on postcondition check: {postSolver.ReasonUnknown} (query budget {solverTimeoutMilliseconds} ms)");
                }
                if (postStatus == Status.SATISFIABLE)
                {
                    var model = postSolver.Model;
                    var counterexample = new Dictionary<string, object>();
                    foreach (var (pName, pair) in inputs)
                    {
                        var bits = ((BitVecNum)model.Eval(pair.Bv, completion: true)).UInt64;
                        counterexample[pName] = unchecked((int)(uint)bits);
                    }

                    object actualResult;
                    if (body is IntEncoded ib)
                    {
                        var resBits = ((BitVecNum)model.Eval(ib.Bv, completion: true)).UInt64;
                        actualResult = unchecked((int)(uint)resBits);
                    }
                    else if (body is BoolEncoded bb)
                    {
                        actualResult = ((BoolExpr)model.Eval(bb.Bv, completion: true)).IsTrue;
                    }
                    else
                    {
                        actualResult = 0;
                    }

                    return new SymbolicCheckResult.ContractViolation(methodName, ensuresClause, counterexample, actualResult);
                }
            }
        }

        return new SymbolicCheckResult.Verified(methodName);
    }

    private static Encoded EncodeBody(Context ctx, DualEncoder encoder, MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody is { } arrow)
            return encoder.Encode(arrow.Expression);

        var statements = method.Body!.Statements;
        return Fold(ctx, encoder, statements, 0);
    }

    private static Encoded Fold(
        Context ctx, DualEncoder encoder, IReadOnlyList<StatementSyntax> statements, int i)
    {
        var stmt = statements[i];
        if (stmt is ReturnStatementSyntax { Expression: { } re })
            return encoder.Encode(re);

        var ifs = (IfStatementSyntax)stmt;
        var cond = AsBool(encoder.Encode(ifs.Condition));
        var then = encoder.Encode(((ReturnStatementSyntax)ifs.Statement).Expression!);
        var rest = Fold(ctx, encoder, statements, i + 1);
        return (then, rest) switch
        {
            (IntEncoded ti, IntEncoded ri) => new IntEncoded(
                (BitVecExpr)ctx.MkITE(cond.Bv, ti.Bv, ri.Bv),
                (BitVecExpr)ctx.MkITE(cond.Ideal, ti.Ideal, ri.Ideal)),
            (BoolEncoded tb, BoolEncoded rb) => new BoolEncoded(
                (BoolExpr)ctx.MkITE(cond.Bv, tb.Bv, rb.Bv),
                (BoolExpr)ctx.MkITE(cond.Ideal, tb.Ideal, rb.Ideal)),
            _ => throw new NotSupportedException("branches in method body have different types")
        };
    }

    private static BoolEncoded AsBool(Encoded e) =>
        e as BoolEncoded ?? throw new NotSupportedException("method does not return a boolean expression");
}

public static class SymbolicMethodLocator
{
    public static IReadOnlyList<LocatedMethod> Enumerate(string targetSourceDirectory)
    {
        if (!System.IO.Directory.Exists(targetSourceDirectory))
            return Array.Empty<LocatedMethod>();

        var located = new List<LocatedMethod>();
        var files = System.IO.Directory.EnumerateFiles(targetSourceDirectory, "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(f => !IsExcluded(f));

        foreach (var file in files)
        {
            string source;
            try { source = System.IO.File.ReadAllText(file); }
            catch { continue; }

            SyntaxTree tree;
            try { tree = CSharpSyntaxTree.ParseText(source); }
            catch { continue; }

            var root = tree.GetRoot();
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (SymbolicMethodChecker.HasContractAttributes(method))
                    located.Add(new LocatedMethod(Owner(method), Signature(method), file, Line(method), method));
            }
        }

        // Directory enumeration order is not guaranteed. The coverage files and verify.sh's block
        // must not vary between runs, so publish a stable order.
        return located
            .OrderBy(location => location.Owner, StringComparer.Ordinal)
            .ThenBy(location => location.Signature, StringComparer.Ordinal)
            .ThenBy(location => location.File, StringComparer.Ordinal)
            .ToList();
    }

    private static string Owner(MethodDeclarationSyntax method)
    {
        var parts = new List<string>();
        for (var node = method.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case TypeDeclarationSyntax type:
                    parts.Add(type.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Add(ns.Name.ToString());
                    break;
            }
        }

        parts.Reverse();
        parts.Add(method.Identifier.Text);
        return string.Join(".", parts);
    }

    private static string Signature(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters
            .Select(parameter => parameter.Type?.ToString() ?? string.Empty);

        return $"{method.Identifier.Text}({string.Join(", ", parameters)})";
    }

    private static int Line(MethodDeclarationSyntax method) =>
        method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static bool IsExcluded(string path)
    {
        var segments = path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "obj" or "bin" or ".git" or "verification" or ".packages");
    }
}

public static class SymbolicMethodChecker
{
    /// <summary>
    /// Every symbolic result with the source location it came from. The results are named after the
    /// canonical owner (namespace, enclosing types, method) rather than the bare identifier, so two
    /// types that each declare the same method are distinguishable in the report.
    /// </summary>
    public static IReadOnlyList<LocatedSymbolicResult> CheckAllWithLocations(string targetSourceDirectory) =>
        SymbolicMethodLocator.Enumerate(targetSourceDirectory)
            .Select(located => new LocatedSymbolicResult(
                located,
                Rename(SymbolicChecker.CheckContracts(located.Method), located.Owner)))
            .ToList();

    public static IReadOnlyList<SymbolicCheckResult> CheckAll(string targetSourceDirectory) =>
        CheckAllWithLocations(targetSourceDirectory).Select(result => result.Result).ToList();

    public static bool HasContractAttributes(MethodDeclarationSyntax method)
    {
        // Seam methods ([Step], [Deterministic]) are checked dynamically by ConformanceChecker,
        // not by the symbolic arithmetic verifier.
        if (HasAttribute(method, "Step") || HasAttribute(method, "Deterministic"))
            return false;

        var contracts = ContractExtractor.Extract(method);
        return contracts.Requires.Count > 0 || contracts.Ensures.Count > 0;
    }

    private static SymbolicCheckResult Rename(SymbolicCheckResult result, string owner) => result switch
    {
        SymbolicCheckResult.Verified verified => verified with { MethodName = owner },
        SymbolicCheckResult.ContractViolation violation => violation with { MethodName = owner },
        SymbolicCheckResult.OverflowDivergence overflow => overflow with { MethodName = owner },
        SymbolicCheckResult.PreconditionContradiction contradiction =>
            contradiction with { MethodName = owner },
        SymbolicCheckResult.Unsupported unsupported => unsupported with { MethodName = owner },
        _ => result,
    };

    private static bool HasAttribute(MethodDeclarationSyntax method, string attributeName)
    {
        return method.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a =>
            {
                var name = a.Name.ToString();
                return name == attributeName
                    || name == attributeName + "Attribute"
                    || name.EndsWith("." + attributeName, StringComparison.Ordinal)
                    || name.EndsWith("." + attributeName + "Attribute", StringComparison.Ordinal);
            });
    }
}
