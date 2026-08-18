using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace Midora.Compiler;

public enum BatchEditField
{
    Velocity,
    PointValue,
    KeyNumber,
    Gate,
    Tick
}

public readonly record struct BatchEditValues(
    double Velocity,
    double PointValue,
    double KeyNumber,
    double Gate,
    double Tick,
    double RelativeTick);

public enum BatchEditFormulaKind
{
    Identity,
    DirectValue,
    Percentage,
    Multiply,
    Divide,
    Add,
    Subtract,
    CSharpExpression
}

public sealed class BatchEditExpressionProgram : IDisposable
{
    private const string ResultVariables = "v1,p1,k1,g1,t1";
    private static readonly IReadOnlyDictionary<BatchEditField, string> OldVariables =
        new Dictionary<BatchEditField, string>
        {
            [BatchEditField.Velocity] = "v0",
            [BatchEditField.PointValue] = "p0",
            [BatchEditField.KeyNumber] = "k0",
            [BatchEditField.Gate] = "g0",
            [BatchEditField.Tick] = "t0"
        };
    private static readonly IReadOnlyDictionary<string, BatchEditField> OldFields =
        OldVariables.ToDictionary(static value => value.Value, static value => value.Key, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, BatchEditField> ResultFields =
        new Dictionary<string, BatchEditField>(StringComparer.Ordinal)
        {
            ["v1"] = BatchEditField.Velocity,
            ["p1"] = BatchEditField.PointValue,
            ["k1"] = BatchEditField.KeyNumber,
            ["g1"] = BatchEditField.Gate,
            ["t1"] = BatchEditField.Tick
        };
    private static readonly HashSet<string> VariableNames =
    [
        "v0", "v1", "p0", "p1", "k0", "k1", "g0", "g1", "t0", "t1", "tr"
    ];
    private static readonly HashSet<string> MathMethodNames = typeof(Math)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.ReturnType != typeof(void))
        .Select(method => method.Name)
        .ToHashSet(StringComparer.Ordinal);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(
        ResolveReferences,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IReadOnlyDictionary<BatchEditField, CompiledFormula> _formulas;
    private readonly IReadOnlyList<BatchEditField> _evaluationOrder;
    private bool _disposed;

    private BatchEditExpressionProgram(
        IReadOnlyDictionary<BatchEditField, CompiledFormula> formulas,
        IReadOnlyList<BatchEditField> evaluationOrder)
    {
        _formulas = formulas;
        _evaluationOrder = evaluationOrder;
    }

    public IReadOnlyCollection<BatchEditField> Fields => _formulas.Keys.ToArray();

    public static BatchEditExpressionProgram Compile(
        IReadOnlyDictionary<BatchEditField, string?> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        if (expressions.Count == 0)
        {
            throw new ArgumentException("At least one batch-edit field is required.", nameof(expressions));
        }
        if (expressions.Keys.Any(field => !Enum.IsDefined(field)))
        {
            throw new ArgumentOutOfRangeException(nameof(expressions));
        }

        HashSet<BatchEditField> availableFields = expressions.Keys.ToHashSet();
        Dictionary<BatchEditField, CompiledFormula> formulas = [];
        try
        {
            foreach ((BatchEditField field, string? text) in expressions)
            {
                formulas.Add(field, CompileFormula(field, text ?? string.Empty, availableFields));
            }
            BatchEditField[] order = BuildEvaluationOrder(formulas);
            return new(formulas, order);
        }
        catch
        {
            foreach (CompiledFormula formula in formulas.Values)
            {
                formula.Dispose();
            }
            throw;
        }
    }

    public BatchEditFormulaKind GetFormulaKind(BatchEditField field) =>
        GetFormula(field).Kind;

    public double? GetConstant(BatchEditField field) => GetFormula(field).Constant;

    public BatchEditValues Evaluate(
        BatchEditValues source,
        Stopwatch timeoutClock,
        TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(timeoutClock);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        BatchEditValues current = source;
        foreach (BatchEditField field in _evaluationOrder)
        {
            if (timeoutClock.Elapsed >= timeout)
            {
                throw new TimeoutException(
                    $"Batch edit evaluation exceeded {timeout.TotalSeconds:0.###} seconds.");
            }
            CompiledFormula formula = _formulas[field];
            double oldValue = Read(source, field);
            double result = formula.Evaluate(oldValue, source, current);
            if (!double.IsFinite(result))
            {
                throw new InvalidOperationException(
                    $"The {field} expression returned a non-finite value.");
            }
            current = Write(current, field, result);
        }
        return current;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (CompiledFormula formula in _formulas.Values)
        {
            formula.Dispose();
        }
    }

    private CompiledFormula GetFormula(BatchEditField field) =>
        _formulas.TryGetValue(field, out CompiledFormula? formula)
            ? formula
            : throw new ArgumentOutOfRangeException(nameof(field));

    private static CompiledFormula CompileFormula(
        BatchEditField field,
        string text,
        IReadOnlySet<BatchEditField> availableFields)
    {
        string value = text.Trim();
        if (value.Length == 0)
        {
            return CompiledFormula.Identity(field);
        }
        if (value[0] == '=')
        {
            string expressionText = value[1..].Trim();
            if (expressionText.Length == 0)
            {
                throw new ArgumentException($"The {field} C# expression is empty.");
            }
            ExpressionSyntax expression = SyntaxFactory.ParseExpression(expressionText);
            Diagnostic? syntaxError = expression.GetDiagnostics()
                .FirstOrDefault(value => value.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            if (syntaxError is not null)
            {
                throw new ArgumentException(
                    $"The {field} expression is invalid: {syntaxError.GetMessage(CultureInfo.InvariantCulture)}");
            }
            ValidateExpressionSyntax(expression, field);
            BatchEditField[] unavailableOldFields = expression
                .DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Where(identifier => OldFields.ContainsKey(identifier.Identifier.ValueText))
                .Select(identifier => OldFields[identifier.Identifier.ValueText])
                .Where(value => !availableFields.Contains(value))
                .Distinct()
                .Order()
                .ToArray();
            if (unavailableOldFields.Length != 0)
            {
                throw new ArgumentException(
                    $"The {field} expression references unavailable source field(s): {string.Join(", ", unavailableOldFields)}.");
            }
            HashSet<BatchEditField> dependencies = expression
                .DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Where(identifier => ResultFields.ContainsKey(identifier.Identifier.ValueText))
                .Select(identifier => ResultFields[identifier.Identifier.ValueText])
                .ToHashSet();
            if (dependencies.Contains(field))
            {
                throw new ArgumentException(
                    $"The {field} expression cannot reference its own result variable. Result variables are {ResultVariables}.");
            }
            return CompileCSharp(field, expressionText, dependencies);
        }

        BatchEditFormulaKind kind;
        string numberText;
        if (value.EndsWith('%'))
        {
            kind = BatchEditFormulaKind.Percentage;
            numberText = value[..^1].Trim();
        }
        else if (value.Length > 1 && value[0] is '*' or '/' or '+' or '-')
        {
            kind = value[0] switch
            {
                '*' => BatchEditFormulaKind.Multiply,
                '/' => BatchEditFormulaKind.Divide,
                '+' => BatchEditFormulaKind.Add,
                '-' => BatchEditFormulaKind.Subtract,
                _ => throw new UnreachableException()
            };
            numberText = value[1..].Trim();
        }
        else
        {
            kind = BatchEditFormulaKind.DirectValue;
            numberText = value;
        }
        if (!double.TryParse(
                numberText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double constant)
            || !double.IsFinite(constant))
        {
            throw new ArgumentException(
                $"The {field} value must use invariant decimal notation.");
        }
        if (kind != BatchEditFormulaKind.DirectValue && constant < 0)
        {
            throw new ArgumentException(
                $"The {field} single-step operand must be non-negative.");
        }
        if (kind == BatchEditFormulaKind.Divide && constant == 0)
        {
            throw new ArgumentException($"The {field} divisor cannot be zero.");
        }
        return CompiledFormula.CreateConstant(field, kind, constant);
    }

    private static CompiledFormula CompileCSharp(
        BatchEditField field,
        string expression,
        IReadOnlySet<BatchEditField> dependencies)
    {
        string typeName = $"BatchExpression_{Guid.NewGuid():N}";
        string source = $$"""
            using System;
            using static System.Math;
            internal static class {{typeName}}
            {
                public static double Evaluate(
                    double v0, double v1, double p0, double p1,
                    double k0, double k1, double g0, double g1,
                    double t0, double t1, double tr)
                    => (double)({{expression}});
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.CSharp14));
        CSharpCompilation compilation = CSharpCompilation.Create(
            $"Midora.BatchExpression.{Guid.NewGuid():N}",
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                deterministic: true,
                allowUnsafe: false));
        using MemoryStream stream = new();
        EmitResult emit = compilation.Emit(stream);
        if (!emit.Success)
        {
            string errors = string.Join(
                Environment.NewLine,
                emit.Diagnostics
                    .Where(value => value.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    .Select(value => value.GetMessage(CultureInfo.InvariantCulture)));
            throw new ArgumentException($"The {field} C# expression cannot compile: {errors}");
        }
        stream.Position = 0;
        BatchExpressionLoadContext loadContext = new();
        try
        {
            Assembly assembly = loadContext.LoadFromStream(stream);
            MethodInfo method = assembly.GetType(typeName, throwOnError: true)!
                .GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("The batch expression entry point is missing.");
            BatchExpressionDelegate evaluate = method.CreateDelegate<BatchExpressionDelegate>();
            return CompiledFormula.CSharp(field, dependencies, loadContext, evaluate);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private static BatchEditField[] BuildEvaluationOrder(
        IReadOnlyDictionary<BatchEditField, CompiledFormula> formulas)
    {
        Dictionary<BatchEditField, VisitState> state = [];
        List<BatchEditField> result = [];
        foreach (BatchEditField field in formulas.Keys.Order())
        {
            Visit(field);
        }
        return result.ToArray();

        void Visit(BatchEditField field)
        {
            if (state.TryGetValue(field, out VisitState current))
            {
                if (current == VisitState.Visiting)
                {
                    throw new ArgumentException(
                        "Batch edit result variables contain a circular dependency.");
                }
                if (current == VisitState.Visited) return;
            }
            state[field] = VisitState.Visiting;
            foreach (BatchEditField dependency in formulas[field].Dependencies.Order())
            {
                if (!formulas.ContainsKey(dependency))
                {
                    throw new ArgumentException(
                        $"The {field} expression references {dependency}'s result, but that field is unavailable in this batch-edit context.");
                }
                Visit(dependency);
            }
            state[field] = VisitState.Visited;
            result.Add(field);
        }
    }

    private static void ValidateExpressionSyntax(ExpressionSyntax expression, BatchEditField field)
    {
        foreach (SyntaxNode node in expression.DescendantNodesAndSelf())
        {
            bool allowed = node switch
            {
                LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.NumericLiteralExpression)
                    || literal.IsKind(SyntaxKind.TrueLiteralExpression)
                    || literal.IsKind(SyntaxKind.FalseLiteralExpression),
                IdentifierNameSyntax identifier => IsAllowedIdentifier(identifier),
                ParenthesizedExpressionSyntax => true,
                PrefixUnaryExpressionSyntax unary => unary.IsKind(SyntaxKind.UnaryPlusExpression)
                    || unary.IsKind(SyntaxKind.UnaryMinusExpression)
                    || unary.IsKind(SyntaxKind.LogicalNotExpression),
                BinaryExpressionSyntax binary => IsAllowedBinary(binary.Kind()),
                ConditionalExpressionSyntax => true,
                InvocationExpressionSyntax invocation => IsAllowedInvocation(invocation),
                ArgumentListSyntax or ArgumentSyntax => true,
                MemberAccessExpressionSyntax member => IsAllowedMemberAccess(member),
                CastExpressionSyntax cast => cast.Type is PredefinedTypeSyntax predefined
                    && predefined.Keyword.IsKind(SyntaxKind.DoubleKeyword),
                PredefinedTypeSyntax predefined => predefined.Keyword.IsKind(SyntaxKind.DoubleKeyword),
                _ => false
            };
            if (!allowed)
            {
                throw new ArgumentException(
                    $"The {field} expression contains unsupported C# syntax '{node.Kind()}'. Only bounded numeric expressions and System.Math calls are allowed.");
            }
        }
    }

    private static bool IsAllowedIdentifier(IdentifierNameSyntax identifier)
    {
        string name = identifier.Identifier.ValueText;
        if (VariableNames.Contains(name) || name is "Math" or "PI" or "E") return true;
        return (identifier.Parent is InvocationExpressionSyntax
                || identifier.Parent is MemberAccessExpressionSyntax)
            && MathMethodNames.Contains(name);
    }

    private static bool IsAllowedInvocation(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier => MathMethodNames.Contains(identifier.Identifier.ValueText),
            MemberAccessExpressionSyntax member => IsAllowedMemberAccess(member)
                && MathMethodNames.Contains(member.Name.Identifier.ValueText),
            _ => false
        };

    private static bool IsAllowedMemberAccess(MemberAccessExpressionSyntax member) =>
        member.Expression is IdentifierNameSyntax { Identifier.ValueText: "Math" }
        && (MathMethodNames.Contains(member.Name.Identifier.ValueText)
            || member.Name.Identifier.ValueText is "PI" or "E");

    private static bool IsAllowedBinary(SyntaxKind kind) => kind is
        SyntaxKind.AddExpression
        or SyntaxKind.SubtractExpression
        or SyntaxKind.MultiplyExpression
        or SyntaxKind.DivideExpression
        or SyntaxKind.ModuloExpression
        or SyntaxKind.LessThanExpression
        or SyntaxKind.LessThanOrEqualExpression
        or SyntaxKind.GreaterThanExpression
        or SyntaxKind.GreaterThanOrEqualExpression
        or SyntaxKind.EqualsExpression
        or SyntaxKind.NotEqualsExpression
        or SyntaxKind.LogicalAndExpression
        or SyntaxKind.LogicalOrExpression;

    private static IReadOnlyList<MetadataReference> ResolveReferences()
    {
        string? trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trusted))
        {
            throw new InvalidOperationException(
                "The runtime did not expose trusted platform assemblies for batch expression compilation.");
        }
        return trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static double Read(BatchEditValues values, BatchEditField field) => field switch
    {
        BatchEditField.Velocity => values.Velocity,
        BatchEditField.PointValue => values.PointValue,
        BatchEditField.KeyNumber => values.KeyNumber,
        BatchEditField.Gate => values.Gate,
        BatchEditField.Tick => values.Tick,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static BatchEditValues Write(
        BatchEditValues values,
        BatchEditField field,
        double value) => field switch
    {
        BatchEditField.Velocity => values with { Velocity = value },
        BatchEditField.PointValue => values with { PointValue = value },
        BatchEditField.KeyNumber => values with { KeyNumber = value },
        BatchEditField.Gate => values with { Gate = value },
        BatchEditField.Tick => values with { Tick = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private sealed class CompiledFormula : IDisposable
    {
        private readonly BatchEditField _field;
        private readonly BatchExpressionLoadContext? _loadContext;
        private readonly BatchExpressionDelegate? _expression;

        private CompiledFormula(
            BatchEditField field,
            BatchEditFormulaKind kind,
            double? constant,
            IReadOnlySet<BatchEditField>? dependencies = null,
            BatchExpressionLoadContext? loadContext = null,
            BatchExpressionDelegate? expression = null)
        {
            _field = field;
            Kind = kind;
            Constant = constant;
            Dependencies = dependencies ?? new HashSet<BatchEditField>();
            _loadContext = loadContext;
            _expression = expression;
        }

        public BatchEditFormulaKind Kind { get; }
        public double? Constant { get; }
        public IReadOnlySet<BatchEditField> Dependencies { get; }

        public static CompiledFormula Identity(BatchEditField field) =>
            new(field, BatchEditFormulaKind.Identity, null);

        public static CompiledFormula CreateConstant(
            BatchEditField field,
            BatchEditFormulaKind kind,
            double value) =>
            new(field, kind, value);

        public static CompiledFormula CSharp(
            BatchEditField field,
            IReadOnlySet<BatchEditField> dependencies,
            BatchExpressionLoadContext loadContext,
            BatchExpressionDelegate expression) =>
            new(
                field,
                BatchEditFormulaKind.CSharpExpression,
                null,
                dependencies,
                loadContext,
                expression);

        public double Evaluate(
            double oldValue,
            BatchEditValues source,
            BatchEditValues current) => Kind switch
        {
            BatchEditFormulaKind.Identity => oldValue,
            BatchEditFormulaKind.DirectValue => Constant!.Value,
            BatchEditFormulaKind.Percentage => oldValue * Constant!.Value / 100,
            BatchEditFormulaKind.Multiply => oldValue * Constant!.Value,
            BatchEditFormulaKind.Divide => oldValue / Constant!.Value,
            BatchEditFormulaKind.Add => oldValue + Constant!.Value,
            BatchEditFormulaKind.Subtract => oldValue - Constant!.Value,
            BatchEditFormulaKind.CSharpExpression => _expression!(
                source.Velocity,
                current.Velocity,
                source.PointValue,
                current.PointValue,
                source.KeyNumber,
                current.KeyNumber,
                source.Gate,
                current.Gate,
                source.Tick,
                current.Tick,
                source.RelativeTick),
            _ => throw new UnreachableException()
        };

        public void Dispose() => _loadContext?.Unload();
    }

    private sealed class BatchExpressionLoadContext()
        : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    private delegate double BatchExpressionDelegate(
        double v0,
        double v1,
        double p0,
        double p1,
        double k0,
        double k1,
        double g0,
        double g1,
        double t0,
        double t1,
        double tr);

    private enum VisitState
    {
        Visiting,
        Visited
    }
}
