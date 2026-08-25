using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Midora.Domain;
using Midora.Mapping.Contract.V2;

namespace Midora.Compiler;

internal sealed class MappingExpressionCompiler : IDisposable
{
    internal delegate double MappingDelegate(double value, in MappingContextV2 context);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CSharpParseOptions ParseOptions = new(
        LanguageVersion.CSharp14,
        DocumentationMode.None,
        SourceCodeKind.Regular);
    private static readonly IReadOnlyDictionary<string, PropertyInfo> ContextProperties =
        typeof(MappingContextV2)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(property => property.Name, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<(string Name, int Arity), MethodInfo> MathMethods =
        CreateMathMethods();
    private static readonly IReadOnlyDictionary<string, double> MathConstants =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [nameof(Math.E)] = Math.E,
            [nameof(Math.PI)] = Math.PI,
            [nameof(Math.Tau)] = Math.Tau
        };

    private readonly Dictionary<CacheKey, CacheEntry> _cache = [];
    private bool _disposed;

    internal int CompilationCount { get; private set; }
    internal int CachedEntryCount => _cache.Count;

    public MappingDelegate GetOrCompile(
        CSharpMappingFunction function,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDefinition(function);
        CacheKey key = CreateKey(function.AbiVersion, function.Body);
        if (!_cache.TryGetValue(key, out CacheEntry? entry))
        {
            entry = Compile(function.Body, cancellationToken);
            _cache.Add(key, entry);
            CompilationCount++;
        }
        if (entry.Error is not null) throw new MappingException(entry.Error);
        return entry.Delegate!;
    }

    public IReadOnlySet<string> GetReferencedContextFields(
        CSharpMappingFunction function,
        CancellationToken cancellationToken = default)
    {
        _ = GetOrCompile(function, cancellationToken);
        CacheKey key = CreateKey(function.AbiVersion, function.Body);
        return _cache[key].ReferencedContextFields;
    }

    public void SynchronizeFunctions(
        IEnumerable<CSharpMappingFunction> functions,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(functions);
        HashSet<CacheKey> live = [];
        foreach (CSharpMappingFunction function in functions.Where(function => function is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                live.Add(CreateKey(function.AbiVersion, function.Body ?? string.Empty));
            }
            catch (EncoderFallbackException)
            {
                // Invalid source cannot own a compiled cache entry.
            }
        }
        foreach (CacheKey stale in _cache.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            _cache.Remove(stale);
        }
    }

    public void Clear() => _cache.Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _cache.Clear();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void ValidateDefinition(CSharpMappingFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (function.AbiVersion != MappingExpressionAbiV3.Version)
        {
            throw new MappingException(
                $"Unsupported Mapping Expression ABI version {function.AbiVersion}; expected {MappingExpressionAbiV3.Version}. Free C# Mapping Functions are not executed.");
        }
        if (string.IsNullOrWhiteSpace(function.Body))
        {
            throw new MappingException("Mapping Function expression cannot be empty.");
        }
        if (function.Body.EnumerateRunes().Count() > MappingExpressionAbiV3.MaximumSourceLength)
        {
            throw new MappingException(
                $"Mapping Function expression exceeds the {MappingExpressionAbiV3.MaximumSourceLength} Unicode-scalar limit.");
        }
        if (function.Body.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new MappingException("Mapping Function expression must be a single line.");
        }
        try
        {
            _ = StrictUtf8.GetByteCount(function.Body);
        }
        catch (EncoderFallbackException exception)
        {
            throw new MappingException(
                "Mapping Function expression must be valid Unicode with an exact UTF-8 encoding.",
                exception);
        }
    }

    private static CacheKey CreateKey(int abiVersion, string expression)
    {
        byte[] input = StrictUtf8.GetBytes(expression);
        string hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return new(abiVersion, MappingExpressionAbiV3.CompilerProfileId, hash);
    }

    private static CacheEntry Compile(string source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(
            source,
            options: ParseOptions,
            consumeFullText: true);
        Diagnostic? syntaxError = syntax.GetDiagnostics()
            .FirstOrDefault(diagnostic =>
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        if (syntaxError is not null)
        {
            return CacheEntry.Failure(
                $"Mapping Function expression is invalid: {syntaxError.GetMessage(CultureInfo.InvariantCulture)}");
        }

        SyntaxNode[] nodes = syntax.DescendantNodesAndSelf().ToArray();
        if (nodes.Length > MappingExpressionAbiV3.MaximumSyntaxNodes)
        {
            return CacheEntry.Failure(
                $"Mapping Function expression exceeds the {MappingExpressionAbiV3.MaximumSyntaxNodes} syntax-node limit.");
        }
        int maximumDepth = nodes.Max(node => node.Ancestors().Count() + 1);
        if (maximumDepth > MappingExpressionAbiV3.MaximumSyntaxDepth)
        {
            return CacheEntry.Failure(
                $"Mapping Function expression exceeds the {MappingExpressionAbiV3.MaximumSyntaxDepth} nesting-depth limit.");
        }

        try
        {
            ParameterExpression value = Expression.Parameter(typeof(double), "value");
            ParameterExpression context = Expression.Parameter(
                typeof(MappingContextV2).MakeByRefType(),
                "context");
            Binder binder = new(value, context);
            BoundExpression bound = binder.Bind(syntax);
            if (bound.Kind != BoundKind.Number)
            {
                return CacheEntry.Failure("Mapping Function expression must return a number.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            MappingDelegate compiled = Expression
                .Lambda<MappingDelegate>(bound.Expression, value, context)
                .Compile();
            cancellationToken.ThrowIfCancellationRequested();
            return CacheEntry.Success(compiled, binder.ReferencedContextFields);
        }
        catch (MappingExpressionBindingException exception)
        {
            return CacheEntry.Failure(exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return CacheEntry.Failure($"Mapping Function expression could not be compiled: {exception.Message}");
        }
    }

    private static IReadOnlyDictionary<(string Name, int Arity), MethodInfo> CreateMathMethods()
    {
        return typeof(Math)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => MappingExpressionLanguageV3.MathMethodNames.Contains(method.Name)
                && method.ReturnType == typeof(double)
                && method.GetParameters().All(parameter => parameter.ParameterType == typeof(double)))
            .GroupBy(method => (method.Name, method.GetParameters().Length))
            .ToDictionary(group => group.Key, group => group.Single());
    }

    private sealed class Binder(
        ParameterExpression valueParameter,
        ParameterExpression contextParameter)
    {
        private readonly HashSet<string> _referencedContextFields = new(StringComparer.Ordinal);

        public IReadOnlySet<string> ReferencedContextFields => _referencedContextFields;

        public BoundExpression Bind(ExpressionSyntax syntax) => syntax switch
        {
            ParenthesizedExpressionSyntax parenthesized => Bind(parenthesized.Expression),
            LiteralExpressionSyntax literal => BindLiteral(literal),
            IdentifierNameSyntax identifier => BindIdentifier(identifier),
            MemberAccessExpressionSyntax member => BindMember(member),
            PrefixUnaryExpressionSyntax unary => BindUnary(unary),
            BinaryExpressionSyntax binary => BindBinary(binary),
            ConditionalExpressionSyntax conditional => BindConditional(conditional),
            InvocationExpressionSyntax invocation => BindInvocation(invocation),
            _ => throw Unsupported(syntax)
        };

        private static BoundExpression BindLiteral(LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
                return new(Expression.Constant(true), BoundKind.Boolean);
            if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
                return new(Expression.Constant(false), BoundKind.Boolean);
            if (!literal.IsKind(SyntaxKind.NumericLiteralExpression)) throw Unsupported(literal);
            try
            {
                double value = Convert.ToDouble(literal.Token.Value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(value))
                    throw new MappingExpressionBindingException("Numeric literals must be finite.");
                return new(Expression.Constant(value), BoundKind.Number);
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                throw new MappingExpressionBindingException(
                    $"Numeric literal '{literal.Token.Text}' cannot be represented as a finite double.");
            }
        }

        private BoundExpression BindIdentifier(IdentifierNameSyntax identifier)
        {
            if (identifier.Identifier.ValueText == "value")
                return new(valueParameter, BoundKind.Number);
            throw new MappingExpressionBindingException(
                $"Identifier '{identifier.Identifier.ValueText}' is not available. Use value, context.<field>, approved enum members, or Math.<member>.");
        }

        private BoundExpression BindMember(MemberAccessExpressionSyntax member)
        {
            if (member.Expression is IdentifierNameSyntax receiver)
            {
                string receiverName = receiver.Identifier.ValueText;
                string memberName = member.Name.Identifier.ValueText;
                if (receiverName == "context") return BindContextMember(memberName);
                if (receiverName == "Math" && MathConstants.TryGetValue(memberName, out double constant))
                    return new(Expression.Constant(constant), BoundKind.Number);
                if (receiverName == nameof(MappingEventKindV2)
                    && Enum.TryParse(memberName, ignoreCase: false, out MappingEventKindV2 eventKind)
                    && Enum.IsDefined(eventKind))
                    return new(Expression.Constant(eventKind), BoundKind.EventKind);
                if (receiverName == nameof(MappingTargetParameterV2)
                    && Enum.TryParse(memberName, ignoreCase: false, out MappingTargetParameterV2 target)
                    && Enum.IsDefined(target))
                    return new(Expression.Constant(target), BoundKind.TargetParameter);
            }
            throw Unsupported(member);
        }

        private BoundExpression BindContextMember(string name)
        {
            if (!ContextProperties.TryGetValue(name, out PropertyInfo? property))
                throw new MappingExpressionBindingException($"Mapping context field '{name}' does not exist.");
            BoundKind kind = MappingExpressionLanguageV3.EnumContextFields.Contains(name)
                && property.PropertyType == typeof(MappingEventKindV2)
                ? BoundKind.EventKind
                : MappingExpressionLanguageV3.EnumContextFields.Contains(name)
                    && property.PropertyType == typeof(MappingTargetParameterV2)
                    ? BoundKind.TargetParameter
                    : MappingExpressionLanguageV3.NumericContextFields.Contains(name)
                        && IsNumeric(property.PropertyType)
                        ? BoundKind.Number
                        : throw new MappingExpressionBindingException(
                            $"Mapping context field '{name}' is not available to bounded numeric expressions.");
            _referencedContextFields.Add(name);
            System.Linq.Expressions.Expression access = Expression.Property(contextParameter, property);
            return kind == BoundKind.Number
                ? new(Expression.Convert(access, typeof(double)), kind)
                : new(access, kind);
        }

        private BoundExpression BindUnary(PrefixUnaryExpressionSyntax unary)
        {
            BoundExpression operand = Bind(unary.Operand);
            return unary.Kind() switch
            {
                SyntaxKind.UnaryPlusExpression when operand.Kind == BoundKind.Number => operand,
                SyntaxKind.UnaryMinusExpression when operand.Kind == BoundKind.Number =>
                    new(Expression.Negate(operand.Expression), BoundKind.Number),
                SyntaxKind.LogicalNotExpression when operand.Kind == BoundKind.Boolean =>
                    new(Expression.Not(operand.Expression), BoundKind.Boolean),
                _ => throw Unsupported(unary)
            };
        }

        private BoundExpression BindBinary(BinaryExpressionSyntax binary)
        {
            BoundExpression left = Bind(binary.Left);
            BoundExpression right = Bind(binary.Right);
            SyntaxKind kind = binary.Kind();
            if (kind is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression
                or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression
                or SyntaxKind.ModuloExpression)
            {
                RequireSame(left, right, BoundKind.Number, binary);
                return new(kind switch
                {
                    SyntaxKind.AddExpression => Expression.Add(left.Expression, right.Expression),
                    SyntaxKind.SubtractExpression => Expression.Subtract(left.Expression, right.Expression),
                    SyntaxKind.MultiplyExpression => Expression.Multiply(left.Expression, right.Expression),
                    SyntaxKind.DivideExpression => Expression.Divide(left.Expression, right.Expression),
                    _ => Expression.Modulo(left.Expression, right.Expression)
                }, BoundKind.Number);
            }
            if (kind is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression)
            {
                RequireSame(left, right, BoundKind.Number, binary);
                return new(kind switch
                {
                    SyntaxKind.LessThanExpression => Expression.LessThan(left.Expression, right.Expression),
                    SyntaxKind.LessThanOrEqualExpression => Expression.LessThanOrEqual(left.Expression, right.Expression),
                    SyntaxKind.GreaterThanExpression => Expression.GreaterThan(left.Expression, right.Expression),
                    _ => Expression.GreaterThanOrEqual(left.Expression, right.Expression)
                }, BoundKind.Boolean);
            }
            if (kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)
            {
                if (left.Kind != right.Kind)
                    throw new MappingExpressionBindingException("Equality operands must have the same type.");
                return new(
                    kind == SyntaxKind.EqualsExpression
                        ? Expression.Equal(left.Expression, right.Expression)
                        : Expression.NotEqual(left.Expression, right.Expression),
                    BoundKind.Boolean);
            }
            if (kind is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression)
            {
                RequireSame(left, right, BoundKind.Boolean, binary);
                return new(
                    kind == SyntaxKind.LogicalAndExpression
                        ? Expression.AndAlso(left.Expression, right.Expression)
                        : Expression.OrElse(left.Expression, right.Expression),
                    BoundKind.Boolean);
            }
            throw Unsupported(binary);
        }

        private BoundExpression BindConditional(ConditionalExpressionSyntax conditional)
        {
            BoundExpression condition = Bind(conditional.Condition);
            BoundExpression whenTrue = Bind(conditional.WhenTrue);
            BoundExpression whenFalse = Bind(conditional.WhenFalse);
            if (condition.Kind != BoundKind.Boolean)
                throw new MappingExpressionBindingException("The conditional test must be Boolean.");
            if (whenTrue.Kind != whenFalse.Kind || whenTrue.Kind != BoundKind.Number)
                throw new MappingExpressionBindingException("Both conditional results must be numeric.");
            return new(
                Expression.Condition(condition.Expression, whenTrue.Expression, whenFalse.Expression),
                BoundKind.Number);
        }

        private BoundExpression BindInvocation(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "Math" }
                } member)
            {
                throw new MappingExpressionBindingException("Only approved Math.<method> calls are available.");
            }
            string name = member.Name.Identifier.ValueText;
            if (!MathMethods.TryGetValue((name, invocation.ArgumentList.Arguments.Count), out MethodInfo? method))
                throw new MappingExpressionBindingException(
                    $"Math.{name} with {invocation.ArgumentList.Arguments.Count} argument(s) is not available.");
            List<System.Linq.Expressions.Expression> arguments = [];
            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                if (argument.NameColon is not null || !argument.RefKindKeyword.IsKind(SyntaxKind.None))
                    throw Unsupported(argument);
                BoundExpression bound = Bind(argument.Expression);
                if (bound.Kind != BoundKind.Number)
                    throw new MappingExpressionBindingException($"Math.{name} arguments must be numeric.");
                arguments.Add(bound.Expression);
            }
            return new(Expression.Call(method, arguments), BoundKind.Number);
        }

        private static void RequireSame(
            BoundExpression left,
            BoundExpression right,
            BoundKind expected,
            SyntaxNode source)
        {
            if (left.Kind != expected || right.Kind != expected) throw Unsupported(source);
        }

        private static bool IsNumeric(Type type) => type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong)
            || type == typeof(float)
            || type == typeof(double);
    }

    private static MappingExpressionBindingException Unsupported(SyntaxNode node) =>
        new($"Mapping Function expression contains unsupported syntax '{node.Kind()}'.");

    private readonly record struct CacheKey(int AbiVersion, string CompilerProfile, string SourceHash);
    private readonly record struct BoundExpression(
        System.Linq.Expressions.Expression Expression,
        BoundKind Kind);

    private sealed record CacheEntry(
        MappingDelegate? Delegate,
        IReadOnlySet<string> ReferencedContextFields,
        string? Error)
    {
        public static CacheEntry Success(
            MappingDelegate value,
            IReadOnlySet<string> referencedContextFields) =>
            new(
                value,
                referencedContextFields.ToHashSet(StringComparer.Ordinal),
                null);

        public static CacheEntry Failure(string error) =>
            new(null, new HashSet<string>(StringComparer.Ordinal), error);
    }

    private enum BoundKind
    {
        Number,
        Boolean,
        EventKind,
        TargetParameter
    }

    private sealed class MappingExpressionBindingException(string message) : Exception(message);
}
