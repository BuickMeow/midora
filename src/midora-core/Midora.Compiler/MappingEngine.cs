using Midora.Domain;
using Midora.Mapping.Contract.V2;

namespace Midora.Compiler;

internal sealed class MappingException : Exception
{
    public MappingException(string message) : base(message) { }
    public MappingException(string message, Exception innerException) : base(message, innerException) { }

    public MidoraId? MappingStepId { get; set; }
    public MidoraId? MappingFunctionId { get; set; }
    public MidoraId? LogicalParameterId { get; set; }
    public MidoraId? EnvelopeId { get; set; }
}

internal sealed class MappingEngine : IDisposable
{
    private readonly MappingExpressionCompiler _expressions = new();

    public void SynchronizeFunctions(
        IEnumerable<CSharpMappingFunction> functions,
        CancellationToken cancellationToken = default) =>
        _expressions.SynchronizeFunctions(functions, cancellationToken);

    public void ClearCache() => _expressions.Clear();
    public void Dispose() => _expressions.Dispose();

    public string? ValidateFunction(
        CSharpMappingFunction function,
        CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlySet<string> referenced = _expressions.GetReferencedContextFields(
                function,
                cancellationToken);
            return referenced.SetEquals(function.DeclaredContextFields)
                ? null
                : "Mapping Function context dependencies do not match the fields inferred from its expression.";
        }
        catch (Exception exception) when (exception is MappingException or InvalidOperationException)
        {
            return exception.Message;
        }
    }

    public double Apply(
        double value,
        IReadOnlyList<ValueMappingStep> steps,
        in MappingContextV2 context,
        IReadOnlyDictionary<MidoraId, double> parameters,
        IReadOnlyDictionary<MidoraId, double> envelopes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        double legalMinimum,
        double legalMaximum,
        double targetDefault,
        MappingOverflow overflow,
        bool allowClamp)
    {
        double current = value;
        ValueMappingStep? lastAppliedStep = null;
        foreach (ValueMappingStep step in steps)
        {
            if (!step.IsEnabled) continue;
            lastAppliedStep = step;
            try
            {
                if (step.Operation == MappingOperation.CustomCSharp)
                {
                    current = EvaluateExpression(step, current, context, functions);
                }
                else
                {
                    double source = GetSource(step, current, context, parameters, envelopes);
                    current = step.Operation switch
                    {
                        MappingOperation.Override => source,
                        MappingOperation.Add => current + source,
                        MappingOperation.Multiply => current * source,
                        MappingOperation.Remap => Remap(source, step, legalMaximum, targetDefault),
                        MappingOperation.Clamp => Math.Clamp(current, step.TargetMinimum, step.TargetMaximum),
                        MappingOperation.Ignore => current,
                        MappingOperation.ConstantPlusValue => step.Constant + source,
                        MappingOperation.ConstantMultiplyValue => step.Constant * source,
                        MappingOperation.ConstantMinusValue => step.Constant - source,
                        MappingOperation.ValueMinusConstant => source - step.Constant,
                        MappingOperation.ConstantDivideValue => Divide(step.Constant, source, step, legalMaximum, targetDefault),
                        MappingOperation.ValueDivideConstant => Divide(source, step.Constant, step, legalMaximum, targetDefault),
                        _ => throw new MappingException($"Unknown mapping operation {step.Operation}.")
                    };
                }

                if (!double.IsFinite(current))
                {
                    throw new MappingException("Mapping produced NaN or Infinity.");
                }
            }
            catch (MappingException exception)
            {
                exception.MappingStepId ??= step.Id;
                exception.MappingFunctionId ??= step.MappingFunctionId;
                exception.LogicalParameterId ??= step.LogicalParameterId;
                exception.EnvelopeId ??= step.EnvelopeId;
                throw;
            }
        }

        if (current < legalMinimum || current > legalMaximum)
        {
            if (overflow == MappingOverflow.Clamp && allowClamp)
            {
                current = Math.Clamp(current, legalMinimum, legalMaximum);
            }
            else
            {
                throw new MappingException($"Mapping result {current} is outside [{legalMinimum}, {legalMaximum}].")
                {
                    MappingStepId = lastAppliedStep?.Id,
                    MappingFunctionId = lastAppliedStep?.MappingFunctionId,
                    LogicalParameterId = lastAppliedStep?.LogicalParameterId,
                    EnvelopeId = lastAppliedStep?.EnvelopeId
                };
            }
        }

        return current;
    }

    public static int Round(double value, MappingRounding rounding) => rounding switch
    {
        MappingRounding.Round => checked((int)Math.Round(value, MidpointRounding.AwayFromZero)),
        MappingRounding.Floor => checked((int)Math.Floor(value)),
        MappingRounding.Ceiling => checked((int)Math.Ceiling(value)),
        _ => throw new MappingException($"Unknown rounding {rounding}.")
    };

    private double EvaluateExpression(
        ValueMappingStep step,
        double current,
        in MappingContextV2 context,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions)
    {
        try
        {
            if (!step.MappingFunctionId.HasValue
                || !functions.TryGetValue(step.MappingFunctionId.Value, out CSharpMappingFunction? function))
            {
                throw new MappingException("The referenced Mapping Function is unavailable.");
            }
            MappingContextV2 invocationContext = context with { CurrentValue = current };
            return _expressions.GetOrCompile(function)(current, in invocationContext);
        }
        catch (MappingException)
        {
            throw;
        }
        catch (Exception exception)
        {
            string typeName = exception.GetType().FullName ?? exception.GetType().Name;
            string detail = string.IsNullOrWhiteSpace(exception.Message)
                ? "No exception message was provided."
                : exception.Message;
            throw new MappingException(
                $"Mapping Function expression evaluation failed: {typeName}: {detail}",
                exception);
        }
    }

    private static double GetSource(
        ValueMappingStep step,
        double current,
        in MappingContextV2 context,
        IReadOnlyDictionary<MidoraId, double> parameters,
        IReadOnlyDictionary<MidoraId, double> envelopes) => step.Source switch
        {
            MappingSource.CurrentValue => current,
            MappingSource.TriggerNote => context.TriggerNote,
            MappingSource.TriggerVelocity => context.TriggerVelocity,
            MappingSource.GateLength => context.GateLength,
            MappingSource.PitchDelta => context.PitchDelta,
            MappingSource.TemplateTick => context.TemplateTick,
            MappingSource.ProjectTick => context.ProjectTick,
            MappingSource.TemplateNote => context.TemplateNote,
            MappingSource.TemplateVelocity => context.TemplateVelocity,
            MappingSource.LogicalParameter when step.LogicalParameterId.HasValue
                && parameters.TryGetValue(step.LogicalParameterId.Value, out double value) => value,
            MappingSource.Envelope when step.EnvelopeId.HasValue
                && envelopes.TryGetValue(step.EnvelopeId.Value, out double value) => value,
            MappingSource.Constant => step.Constant,
            MappingSource.LogicalParameter => throw new MappingException("The referenced Logical Parameter is unavailable."),
            MappingSource.Envelope => throw new MappingException("The referenced Envelope is unavailable."),
            _ => throw new MappingException($"Unknown mapping source {step.Source}.")
        };

    private static double Remap(
        double value,
        ValueMappingStep step,
        double legalMaximum,
        double targetDefault)
    {
        if (step.SourceMaximum == step.SourceMinimum)
        {
            return Divide(0, 0, step, legalMaximum, targetDefault);
        }
        double input = step.InputOverflow switch
        {
            MappingInputOverflow.Clamp => Math.Clamp(value, step.SourceMinimum, step.SourceMaximum),
            MappingInputOverflow.Extrapolate => value,
            MappingInputOverflow.Fail when value < step.SourceMinimum || value > step.SourceMaximum =>
                throw new MappingException($"Mapping input {value} is outside [{step.SourceMinimum}, {step.SourceMaximum}]."),
            MappingInputOverflow.Fail => value,
            _ => throw new MappingException($"Unknown input overflow policy {step.InputOverflow}.")
        };
        double ratio = (input - step.SourceMinimum) / (step.SourceMaximum - step.SourceMinimum);
        return step.TargetMinimum + (ratio * (step.TargetMaximum - step.TargetMinimum));
    }

    private static double Divide(
        double numerator,
        double denominator,
        ValueMappingStep step,
        double legalMaximum,
        double targetDefault)
    {
        if (denominator != 0) return numerator / denominator;
        return step.DivideByZero switch
        {
            DivideByZeroPolicy.TargetMaximum => legalMaximum,
            DivideByZeroPolicy.TargetDefault => targetDefault,
            DivideByZeroPolicy.Zero => 0,
            DivideByZeroPolicy.Fail => throw new MappingException("Mapping division by zero."),
            _ => throw new MappingException("Mapping division by zero has no usable fallback.")
        };
    }
}
