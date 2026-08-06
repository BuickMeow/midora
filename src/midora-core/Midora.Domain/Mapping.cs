using Midora.Mapping.Contract.V1;

namespace Midora.Domain;

public enum LogicalParameterType
{
    Integer,
    Double,
    Enum
}

public sealed class LogicalParameterDefinition
{
    public LogicalParameterDefinition(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    public MidoraId Id { get; init; }
    public required string Name { get; set; }
    public LogicalParameterType Type { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 1;
    public double DisplayMinimum { get; set; }
    public double DisplayMaximum { get; set; } = 1;
    public double DefaultValue { get; set; }
    public bool UsesExplicitEnumValues { get; set; }
    public List<LogicalParameterEnumItem> EnumItems { get; } = [];
}

public sealed class LogicalParameterEnumItem
{
    public LogicalParameterEnumItem(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    public MidoraId Id { get; init; }
    public required string Name { get; set; }
    public int Value { get; set; }
}

public enum MappingSource
{
    CurrentValue,
    TriggerNote,
    TriggerVelocity,
    GateLength,
    PitchDelta,
    TemplateTick,
    ProjectTick,
    TemplateNote,
    TemplateVelocity,
    LogicalParameter,
    Envelope,
    Constant
}

public enum MappingOperation
{
    Override,
    Add,
    Multiply,
    Remap,
    Clamp,
    Ignore,
    ConstantPlusValue,
    ConstantMultiplyValue,
    ConstantMinusValue,
    ValueMinusConstant,
    ConstantDivideValue,
    ValueDivideConstant,
    CustomCSharp
}

public enum MappingRounding
{
    Round,
    Floor,
    Ceiling
}

public enum MappingOverflow
{
    Fail,
    Clamp
}

public sealed class MidiIntegerTargetSettings
{
    public MappingRounding Rounding { get; set; } = MappingRounding.Round;
    public MappingOverflow Overflow { get; set; } = MappingOverflow.Fail;
}

public enum MappingInputOverflow
{
    Clamp,
    Extrapolate,
    Fail
}

public enum DivideByZeroPolicy
{
    TargetMaximum,
    TargetDefault,
    Zero,
    Fail
}

public sealed class CSharpMappingFunction
{
    public CSharpMappingFunction(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    public MidoraId Id { get; init; }
    public required string Name { get; set; }
    public required string Body { get; set; }
    public int AbiVersion { get; set; } = MappingAbiV1.Version;
    public HashSet<string> DeclaredContextFields { get; } = new(StringComparer.Ordinal);
}

public sealed class MappingChain : IList<ValueMappingStep>, IReadOnlyList<ValueMappingStep>
{
    private readonly List<ValueMappingStep> _steps = [];

    public MappingChain(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    public MidoraId Id { get; init; }
    public bool IsEnabled { get; set; } = true;
    public int Count => _steps.Count;
    public bool IsReadOnly => false;
    public ValueMappingStep this[int index]
    {
        get => _steps[index];
        set => _steps[index] = value ?? throw new ArgumentNullException(nameof(value));
    }
    public void Add(ValueMappingStep step) => _steps.Add(step);
    public void Clear() => _steps.Clear();
    public bool Contains(ValueMappingStep step) => _steps.Contains(step);
    public void CopyTo(ValueMappingStep[] array, int arrayIndex) => _steps.CopyTo(array, arrayIndex);
    public int IndexOf(ValueMappingStep step) => _steps.IndexOf(step);
    public void Insert(int index, ValueMappingStep step) => _steps.Insert(index, step);
    public bool Remove(ValueMappingStep step) => _steps.Remove(step);
    public void RemoveAt(int index) => _steps.RemoveAt(index);
    public IEnumerator<ValueMappingStep> GetEnumerator() => _steps.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class ValueMappingStep
{
    public ValueMappingStep(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    public MidoraId Id { get; init; }
    public bool IsEnabled { get; set; } = true;
    public MappingSource Source { get; set; } = MappingSource.CurrentValue;
    public MappingOperation Operation { get; set; } = MappingOperation.Add;
    public MidoraId? LogicalParameterId { get; set; }
    public MidoraId? EnvelopeId { get; set; }
    public MidoraId? MappingFunctionId { get; set; }
    public double Constant { get; set; }
    public double SourceMinimum { get; set; }
    public double SourceMaximum { get; set; } = 1;
    public double TargetMinimum { get; set; }
    public double TargetMaximum { get; set; } = 127;
    public MappingInputOverflow InputOverflow { get; set; } = MappingInputOverflow.Clamp;
    public DivideByZeroPolicy DivideByZero { get; set; } = DivideByZeroPolicy.TargetMaximum;
}

public sealed class LogicalParameterMapping
{
    public LogicalParameterMapping(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Steps = new MappingChain(project);
        TargetSettings = new MidiIntegerTargetSettings();
    }

    public MidoraId Id { get; init; }
    public MidoraId ParameterId { get; set; }
    public MidoraId SubVoiceId { get; set; }
    public MidiValueTarget Target { get; set; }
    public MappingChain Steps { get; }
    public MidiIntegerTargetSettings TargetSettings { get; }
}
