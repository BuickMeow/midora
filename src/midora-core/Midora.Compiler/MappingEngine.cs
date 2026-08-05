using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Midora.Domain;

namespace Midora.Compiler;

internal sealed class MappingException : Exception
{
    public MappingException(string message) : base(message)
    {
    }

    public MappingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

internal sealed class MappingEngine
{
    private readonly CSharpMappingCompiler _csharp = new();

    public string? ValidateFunction(CSharpMappingFunction function)
    {
        try
        {
            _ = _csharp.GetOrCompile(function.Body);
            return null;
        }
        catch (Exception exception) when (exception is MappingException or InvalidOperationException)
        {
            return exception.Message;
        }
    }

    public double Apply(
        double value,
        IReadOnlyList<ValueMappingStep> steps,
        in MappingContext context,
        IReadOnlyDictionary<MidoraId, double> parameters,
        IReadOnlyDictionary<MidoraId, double> envelopes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        double legalMinimum,
        double legalMaximum,
        double targetDefault,
        bool allowClamp)
    {
        double current = value;
        foreach (ValueMappingStep step in steps)
        {
            if (!step.IsEnabled)
            {
                continue;
            }
            double source = GetSource(step, current, context, parameters, envelopes);
            current = step.Operation switch
            {
                MappingOperation.Override => source,
                MappingOperation.Add => current + source,
                MappingOperation.Multiply => current * source,
                MappingOperation.Remap => Remap(source, step),
                MappingOperation.Clamp => Math.Clamp(current, step.TargetMinimum, step.TargetMaximum),
                MappingOperation.Ignore => current,
                MappingOperation.ConstantPlusValue => step.Constant + source,
                MappingOperation.ConstantMultiplyValue => step.Constant * source,
                MappingOperation.ConstantMinusValue => step.Constant - source,
                MappingOperation.ValueMinusConstant => source - step.Constant,
                MappingOperation.ConstantDivideValue => Divide(step.Constant, source, step, legalMaximum, targetDefault),
                MappingOperation.ValueDivideConstant => Divide(source, step.Constant, step, legalMaximum, targetDefault),
                MappingOperation.CustomCSharp => EvaluateCSharp(step, current, context, functions),
                _ => throw new MappingException($"Unknown mapping operation {step.Operation}.")
            };

            if (!double.IsFinite(current))
            {
                throw new MappingException("Mapping produced NaN or Infinity.");
            }

        }

        if (current < legalMinimum || current > legalMaximum)
        {
            ValueMappingStep? finalStep = steps.LastOrDefault(value => value.IsEnabled);
            if (finalStep?.Overflow == MappingOverflow.Clamp && allowClamp)
            {
                current = Math.Clamp(current, legalMinimum, legalMaximum);
            }
            else
            {
                throw new MappingException($"Mapping result {current} is outside [{legalMinimum}, {legalMaximum}].");
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

    private double EvaluateCSharp(
        ValueMappingStep step,
        double current,
        in MappingContext context,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions)
    {
        try
        {
            if (!step.MappingFunctionId.HasValue
                || !functions.TryGetValue(step.MappingFunctionId.Value, out CSharpMappingFunction? function))
            {
                throw new MappingException($"Mapping Function '{step.MappingFunctionId}' is unavailable.");
            }
            return _csharp.GetOrCompile(function.Body)(current, context);
        }
        catch (MappingException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MappingException("C# Mapping execution failed.", exception);
        }
    }

    private static double GetSource(
        ValueMappingStep step,
        double current,
        in MappingContext context,
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
        MappingSource.LogicalParameter => throw new MappingException($"Logical Parameter '{step.LogicalParameterId}' is unavailable."),
        MappingSource.Envelope => throw new MappingException($"Envelope '{step.EnvelopeId}' is unavailable."),
        _ => throw new MappingException($"Unknown mapping source {step.Source}.")
    };

    private static double Remap(double value, ValueMappingStep step)
    {
        if (step.SourceMaximum == step.SourceMinimum)
        {
            throw new MappingException("A remap source range cannot be empty.");
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
        if (denominator != 0)
        {
            return numerator / denominator;
        }

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

internal sealed class CSharpMappingCompiler
{
    internal delegate double MappingDelegate(double value, MappingContext context);

    private readonly ConcurrentDictionary<string, MappingDelegate> _cache = new(StringComparer.Ordinal);

    public MappingDelegate GetOrCompile(string body) => _cache.GetOrAdd(body, Compile);

    private static MappingDelegate Compile(string body)
    {
        string source = $$"""
            using System;
            using Midora.Domain;
            public static class MidoraGeneratedMapping
            {
                public static double Transform(double value, MappingContext context)
                {
                    {{body}}
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        List<MetadataReference> references = [];
        string? trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trusted is not null)
        {
            foreach (string path in trusted.Split(Path.PathSeparator))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }
        references.Add(MetadataReference.CreateFromFile(typeof(MappingContext).Assembly.Location));
        CSharpCompilation compilation = CSharpCompilation.Create(
            $"Midora.Mapping.{Guid.NewGuid():N}",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);
        if (!result.Success)
        {
            string text = string.Join(Environment.NewLine, result.Diagnostics
                .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString()));
            throw new MappingException($"C# Mapping compilation failed: {text}");
        }
        stream.Position = 0;
        Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        MethodInfo method = assembly.GetType("MidoraGeneratedMapping")!.GetMethod("Transform", BindingFlags.Public | BindingFlags.Static)!;
        return method.CreateDelegate<MappingDelegate>();
    }
}
