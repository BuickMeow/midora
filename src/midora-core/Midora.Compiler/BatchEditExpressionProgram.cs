using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    BoundedExpression
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
    private static readonly IReadOnlyDictionary<(string Name, int Arity), MethodInfo[]> MathMethods = typeof(Math)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => !method.IsGenericMethod
            && IsNumericType(method.ReturnType)
            && method.GetParameters().All(parameter =>
                !parameter.ParameterType.IsByRef
                && IsNumericType(parameter.ParameterType)))
        .GroupBy(method => (method.Name, method.GetParameters().Length))
        .ToDictionary(
            group => group.Key,
            group => group.OrderBy(method => method.ToString(), StringComparer.Ordinal).ToArray());
    private static readonly HashSet<string> MathMethodNames = MathMethods.Keys
        .Select(value => value.Name)
        .ToHashSet(StringComparer.Ordinal);

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
        foreach ((BatchEditField field, string? text) in expressions)
        {
            formulas.Add(field, CompileFormula(field, text ?? string.Empty, availableFields));
        }
        BatchEditField[] order = BuildEvaluationOrder(formulas);
        return new(formulas, order);
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
            return CompiledFormula.Identity();
        }
        if (value[0] == '=')
        {
            string expressionText = value[1..].Trim();
            if (expressionText.Length == 0)
            {
                throw new ArgumentException($"The {field} expression is empty.");
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
            return CompileExpression(field, expression, dependencies);
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
        return CompiledFormula.CreateConstant(kind, constant);
    }

    private static CompiledFormula CompileExpression(
        BatchEditField field,
        ExpressionSyntax syntax,
        IReadOnlySet<BatchEditField> dependencies)
    {
        ParameterExpression[] parameters = VariableNames
            .OrderBy(GetVariablePosition)
            .Select(name => Expression.Parameter(typeof(double), name))
            .ToArray();
        IReadOnlyDictionary<string, ParameterExpression> variables = parameters
            .ToDictionary(parameter => parameter.Name!, StringComparer.Ordinal);
        try
        {
            BoundExpression bound = new BatchExpressionBinder(variables).Bind(syntax);
            if (bound.Kind != BoundKind.Number)
            {
                throw new BatchExpressionBindingException(
                    "The expression result must be numeric.");
            }
            System.Linq.Expressions.Expression result = bound.Expression.Type == typeof(double)
                ? bound.Expression
                : Expression.Convert(bound.Expression, typeof(double));
            BatchExpressionDelegate evaluate = Expression
                .Lambda<BatchExpressionDelegate>(result, parameters)
                .Compile();
            return CompiledFormula.Expression(dependencies, evaluate);
        }
        catch (BatchExpressionBindingException exception)
        {
            throw new ArgumentException(
                $"The {field} expression cannot compile: {exception.Message}",
                exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException(
                $"The {field} expression cannot compile: {exception.Message}",
                exception);
        }
    }

    private static int GetVariablePosition(string name) => name switch
    {
        "v0" => 0,
        "v1" => 1,
        "p0" => 2,
        "p1" => 3,
        "k0" => 4,
        "k1" => 5,
        "g0" => 6,
        "g1" => 7,
        "t0" => 8,
        "t1" => 9,
        "tr" => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

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

    private static bool IsNumericType(Type type) => type == typeof(sbyte)
        || type == typeof(byte)
        || type == typeof(short)
        || type == typeof(ushort)
        || type == typeof(int)
        || type == typeof(uint)
        || type == typeof(long)
        || type == typeof(ulong)
        || type == typeof(float)
        || type == typeof(double)
        || type == typeof(decimal);

    private sealed class BatchExpressionBinder(
        IReadOnlyDictionary<string, ParameterExpression> variables)
    {
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
            CastExpressionSyntax cast => BindCast(cast),
            _ => throw Unsupported(syntax)
        };

        private static BoundExpression BindLiteral(LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
                return new(Expression.Constant(true), BoundKind.Boolean);
            if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
                return new(Expression.Constant(false), BoundKind.Boolean);
            if (!literal.IsKind(SyntaxKind.NumericLiteralExpression)
                || literal.Token.Value is not { } value
                || !IsNumericType(value.GetType()))
            {
                throw Unsupported(literal);
            }
            if (value is double doubleValue && !double.IsFinite(doubleValue)
                || value is float floatValue && !float.IsFinite(floatValue))
            {
                throw new BatchExpressionBindingException(
                    $"Numeric literal '{literal.Token.Text}' must be finite.");
            }
            return new(Expression.Constant(value, value.GetType()), BoundKind.Number);
        }

        private BoundExpression BindIdentifier(IdentifierNameSyntax identifier)
        {
            string name = identifier.Identifier.ValueText;
            if (variables.TryGetValue(name, out ParameterExpression? variable))
                return new(variable, BoundKind.Number);
            return name switch
            {
                "PI" => new(Expression.Constant(Math.PI), BoundKind.Number),
                "E" => new(Expression.Constant(Math.E), BoundKind.Number),
                _ => throw new BatchExpressionBindingException(
                    $"Identifier '{name}' is not available to Batch Edit expressions.")
            };
        }

        private static BoundExpression BindMember(MemberAccessExpressionSyntax member)
        {
            if (member.Expression is IdentifierNameSyntax { Identifier.ValueText: "Math" })
            {
                return member.Name.Identifier.ValueText switch
                {
                    "PI" => new(Expression.Constant(Math.PI), BoundKind.Number),
                    "E" => new(Expression.Constant(Math.E), BoundKind.Number),
                    _ => throw Unsupported(member)
                };
            }
            throw Unsupported(member);
        }

        private BoundExpression BindUnary(PrefixUnaryExpressionSyntax unary)
        {
            BoundExpression operand = Bind(unary.Operand);
            if (unary.IsKind(SyntaxKind.LogicalNotExpression))
            {
                Require(operand, BoundKind.Boolean, unary);
                return new(Expression.Not(operand.Expression), BoundKind.Boolean);
            }
            Require(operand, BoundKind.Number, unary);
            Type targetType = PromoteUnaryType(operand.Expression.Type, unary.Kind());
            System.Linq.Expressions.Expression promoted = ConvertIfNeeded(operand.Expression, targetType);
            return unary.Kind() switch
            {
                SyntaxKind.UnaryPlusExpression => new(promoted, BoundKind.Number),
                SyntaxKind.UnaryMinusExpression => new(Expression.Negate(promoted), BoundKind.Number),
                _ => throw Unsupported(unary)
            };
        }

        private BoundExpression BindBinary(BinaryExpressionSyntax binary)
        {
            BoundExpression left = Bind(binary.Left);
            BoundExpression right = Bind(binary.Right);
            SyntaxKind kind = binary.Kind();
            if (kind is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression)
            {
                Require(left, BoundKind.Boolean, binary.Left);
                Require(right, BoundKind.Boolean, binary.Right);
                return new(
                    kind == SyntaxKind.LogicalAndExpression
                        ? Expression.AndAlso(left.Expression, right.Expression)
                        : Expression.OrElse(left.Expression, right.Expression),
                    BoundKind.Boolean);
            }
            if ((kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)
                && left.Kind == BoundKind.Boolean && right.Kind == BoundKind.Boolean)
            {
                return new(
                    kind == SyntaxKind.EqualsExpression
                        ? Expression.Equal(left.Expression, right.Expression)
                        : Expression.NotEqual(left.Expression, right.Expression),
                    BoundKind.Boolean);
            }
            Require(left, BoundKind.Number, binary.Left);
            Require(right, BoundKind.Number, binary.Right);
            (System.Linq.Expressions.Expression promotedLeft,
                System.Linq.Expressions.Expression promotedRight) =
                PromoteBinary(left.Expression, right.Expression, binary);
            return kind switch
            {
                SyntaxKind.AddExpression => new(Expression.Add(promotedLeft, promotedRight), BoundKind.Number),
                SyntaxKind.SubtractExpression => new(Expression.Subtract(promotedLeft, promotedRight), BoundKind.Number),
                SyntaxKind.MultiplyExpression => new(Expression.Multiply(promotedLeft, promotedRight), BoundKind.Number),
                SyntaxKind.DivideExpression => new(Expression.Divide(promotedLeft, promotedRight), BoundKind.Number),
                SyntaxKind.ModuloExpression => new(Expression.Modulo(promotedLeft, promotedRight), BoundKind.Number),
                SyntaxKind.LessThanExpression => new(Expression.LessThan(promotedLeft, promotedRight), BoundKind.Boolean),
                SyntaxKind.LessThanOrEqualExpression => new(Expression.LessThanOrEqual(promotedLeft, promotedRight), BoundKind.Boolean),
                SyntaxKind.GreaterThanExpression => new(Expression.GreaterThan(promotedLeft, promotedRight), BoundKind.Boolean),
                SyntaxKind.GreaterThanOrEqualExpression => new(Expression.GreaterThanOrEqual(promotedLeft, promotedRight), BoundKind.Boolean),
                SyntaxKind.EqualsExpression => new(Expression.Equal(promotedLeft, promotedRight), BoundKind.Boolean),
                SyntaxKind.NotEqualsExpression => new(Expression.NotEqual(promotedLeft, promotedRight), BoundKind.Boolean),
                _ => throw Unsupported(binary)
            };
        }

        private BoundExpression BindConditional(ConditionalExpressionSyntax conditional)
        {
            BoundExpression condition = Bind(conditional.Condition);
            Require(condition, BoundKind.Boolean, conditional.Condition);
            BoundExpression whenTrue = Bind(conditional.WhenTrue);
            BoundExpression whenFalse = Bind(conditional.WhenFalse);
            if (whenTrue.Kind != whenFalse.Kind)
            {
                throw new BatchExpressionBindingException(
                    "Both conditional branches must have the same type.");
            }
            if (whenTrue.Kind == BoundKind.Boolean)
            {
                return new(
                    Expression.Condition(condition.Expression, whenTrue.Expression, whenFalse.Expression),
                    BoundKind.Boolean);
            }
            (System.Linq.Expressions.Expression promotedTrue,
                System.Linq.Expressions.Expression promotedFalse) =
                PromoteBinary(whenTrue.Expression, whenFalse.Expression, conditional);
            return new(
                Expression.Condition(condition.Expression, promotedTrue, promotedFalse),
                BoundKind.Number);
        }

        private BoundExpression BindInvocation(InvocationExpressionSyntax invocation)
        {
            string name = invocation.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "Math" }
                } member => member.Name.Identifier.ValueText,
                _ => throw new BatchExpressionBindingException(
                    "Only supported System.Math calls, with an optional Math. prefix, are available.")
            };
            BoundExpression[] arguments = invocation.ArgumentList.Arguments
                .Select(argument =>
                {
                    if (argument.NameColon is not null
                        || !argument.RefKindKeyword.IsKind(SyntaxKind.None))
                    {
                        throw Unsupported(argument);
                    }
                    BoundExpression bound = Bind(argument.Expression);
                    Require(bound, BoundKind.Number, argument.Expression);
                    return bound;
                })
                .ToArray();
            if (!MathMethods.TryGetValue((name, arguments.Length), out MethodInfo[]? candidates))
            {
                throw new BatchExpressionBindingException(
                    $"System.Math method '{name}' with {arguments.Length} argument(s) is not available.");
            }
            MethodCandidate[] applicable = candidates
                .Select(method => TryCreateCandidate(method, arguments))
                .OfType<MethodCandidate>()
                .ToArray();
            if (applicable.Length == 0)
            {
                throw new BatchExpressionBindingException(
                    $"No System.Math overload '{name}' accepts the supplied numeric argument types.");
            }
            MethodCandidate[] best = applicable
                .Where(candidate => applicable.All(other =>
                    ReferenceEquals(candidate, other)
                    || CompareCandidates(candidate, other, arguments) >= 0))
                .ToArray();
            if (best.Length != 1)
            {
                throw new BatchExpressionBindingException(
                    $"System.Math call '{name}' is ambiguous for the supplied numeric argument types.");
            }
            MethodCandidate selected = best[0];
            System.Linq.Expressions.Expression call = Expression.Call(
                selected.Method,
                arguments.Select((argument, index) =>
                    ConvertIfNeeded(argument.Expression, selected.ParameterTypes[index])));
            return new(call, BoundKind.Number);
        }

        private BoundExpression BindCast(CastExpressionSyntax cast)
        {
            BoundExpression value = Bind(cast.Expression);
            Require(value, BoundKind.Number, cast.Expression);
            return new(ConvertIfNeeded(value.Expression, typeof(double)), BoundKind.Number);
        }

        private static MethodCandidate? TryCreateCandidate(
            MethodInfo method,
            IReadOnlyList<BoundExpression> arguments)
        {
            Type[] parameterTypes = method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray();
            for (int index = 0; index < arguments.Count; index++)
            {
                if (!HasImplicitNumericConversion(
                        arguments[index].Expression.Type,
                        parameterTypes[index]))
                {
                    return null;
                }
            }
            return new(method, parameterTypes);
        }

        private static int CompareCandidates(
            MethodCandidate left,
            MethodCandidate right,
            IReadOnlyList<BoundExpression> arguments)
        {
            bool leftBetter = false;
            bool rightBetter = false;
            for (int index = 0; index < arguments.Count; index++)
            {
                Type source = arguments[index].Expression.Type;
                Type leftTarget = left.ParameterTypes[index];
                Type rightTarget = right.ParameterTypes[index];
                if (leftTarget == rightTarget) continue;
                if (source == leftTarget)
                {
                    leftBetter = true;
                    continue;
                }
                if (source == rightTarget)
                {
                    rightBetter = true;
                    continue;
                }
                bool leftToRight = HasImplicitNumericConversion(leftTarget, rightTarget);
                bool rightToLeft = HasImplicitNumericConversion(rightTarget, leftTarget);
                if (leftToRight && !rightToLeft) leftBetter = true;
                if (rightToLeft && !leftToRight) rightBetter = true;
            }
            if (leftBetter == rightBetter) return 0;
            return leftBetter ? 1 : -1;
        }

        private static Type PromoteUnaryType(Type type, SyntaxKind kind)
        {
            if (type == typeof(sbyte) || type == typeof(byte)
                || type == typeof(short) || type == typeof(ushort))
            {
                return typeof(int);
            }
            if (kind == SyntaxKind.UnaryMinusExpression && type == typeof(uint))
                return typeof(long);
            if (kind == SyntaxKind.UnaryMinusExpression && type == typeof(ulong))
            {
                throw new BatchExpressionBindingException(
                    "Unary minus is not defined for an unsigned 64-bit value.");
            }
            return type;
        }

        private static (
            System.Linq.Expressions.Expression Left,
            System.Linq.Expressions.Expression Right) PromoteBinary(
            System.Linq.Expressions.Expression left,
            System.Linq.Expressions.Expression right,
            SyntaxNode source)
        {
            Type target = GetBinaryPromotionType(left.Type, right.Type, source);
            return (ConvertIfNeeded(left, target), ConvertIfNeeded(right, target));
        }

        private static Type GetBinaryPromotionType(Type left, Type right, SyntaxNode source)
        {
            if (left == typeof(decimal) || right == typeof(decimal))
            {
                if (left == typeof(double) || right == typeof(double)
                    || left == typeof(float) || right == typeof(float))
                {
                    throw new BatchExpressionBindingException(
                        "Decimal values cannot be combined directly with float or double values.");
                }
                return typeof(decimal);
            }
            if (left == typeof(double) || right == typeof(double)) return typeof(double);
            if (left == typeof(float) || right == typeof(float)) return typeof(float);
            if (left == typeof(ulong) || right == typeof(ulong))
            {
                Type other = left == typeof(ulong) ? right : left;
                if (other == typeof(sbyte) || other == typeof(short)
                    || other == typeof(int) || other == typeof(long))
                {
                    throw new BatchExpressionBindingException(
                        "Unsigned 64-bit values cannot be combined with signed integral values.");
                }
                return typeof(ulong);
            }
            if (left == typeof(long) || right == typeof(long)) return typeof(long);
            if (left == typeof(uint) || right == typeof(uint))
            {
                Type other = left == typeof(uint) ? right : left;
                return other == typeof(sbyte) || other == typeof(short) || other == typeof(int)
                    ? typeof(long)
                    : typeof(uint);
            }
            if (!IsNumericType(left) || !IsNumericType(right)) throw Unsupported(source);
            return typeof(int);
        }

        private static bool HasImplicitNumericConversion(Type source, Type target)
        {
            if (source == target) return true;
            Type[] targets = source == typeof(sbyte)
                ? [typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)]
                : source == typeof(byte)
                    ? [typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)]
                    : source == typeof(short)
                        ? [typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)]
                        : source == typeof(ushort)
                            ? [typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)]
                            : source == typeof(int)
                                ? [typeof(long), typeof(float), typeof(double), typeof(decimal)]
                                : source == typeof(uint)
                                    ? [typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)]
                                    : source == typeof(long)
                                        ? [typeof(float), typeof(double), typeof(decimal)]
                                        : source == typeof(ulong)
                                            ? [typeof(float), typeof(double), typeof(decimal)]
                                            : source == typeof(float)
                                                ? [typeof(double)]
                                                : [];
            return Array.IndexOf(targets, target) >= 0;
        }

        private static System.Linq.Expressions.Expression ConvertIfNeeded(
            System.Linq.Expressions.Expression expression,
            Type target) => expression.Type == target
                ? expression
                : Expression.Convert(expression, target);

        private static void Require(BoundExpression value, BoundKind expected, SyntaxNode source)
        {
            if (value.Kind != expected) throw Unsupported(source);
        }

        private static BatchExpressionBindingException Unsupported(SyntaxNode node) =>
            new($"Unsupported Batch Edit expression syntax '{node.Kind()}'.");
    }

    private readonly record struct BoundExpression(
        System.Linq.Expressions.Expression Expression,
        BoundKind Kind);

    private sealed record MethodCandidate(
        MethodInfo Method,
        Type[] ParameterTypes);

    private sealed class BatchExpressionBindingException(string message) : Exception(message);

    private enum BoundKind
    {
        Number,
        Boolean
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

    private sealed class CompiledFormula
    {
        private readonly BatchExpressionDelegate? _expression;

        private CompiledFormula(
            BatchEditFormulaKind kind,
            double? constant,
            IReadOnlySet<BatchEditField>? dependencies = null,
            BatchExpressionDelegate? expression = null)
        {
            Kind = kind;
            Constant = constant;
            Dependencies = dependencies ?? new HashSet<BatchEditField>();
            _expression = expression;
        }

        public BatchEditFormulaKind Kind { get; }
        public double? Constant { get; }
        public IReadOnlySet<BatchEditField> Dependencies { get; }

        public static CompiledFormula Identity() =>
            new(BatchEditFormulaKind.Identity, null);

        public static CompiledFormula CreateConstant(
            BatchEditFormulaKind kind,
            double value) =>
            new(kind, value);

        public static CompiledFormula Expression(
            IReadOnlySet<BatchEditField> dependencies,
            BatchExpressionDelegate expression) =>
            new(
                BatchEditFormulaKind.BoundedExpression,
                null,
                dependencies,
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
                BatchEditFormulaKind.BoundedExpression => _expression!(
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
