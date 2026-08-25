using Midora.Domain;
using Midora.Mapping.Contract.V2;
using Xunit;

namespace Midora.Compiler.Tests;

public sealed class MappingExpressionAbiV3Tests
{
    [Fact]
    public void ProfileIsBoundedVersionedAndDoesNotUseTheFreeCSharpProfile()
    {
        Assert.Equal(3, MappingExpressionAbiV3.Version);
        Assert.Equal("midora-bounded-mapping-expression-v3", MappingExpressionAbiV3.CompilerProfileId);
        Assert.Equal(8192, MappingExpressionAbiV3.MaximumSourceLength);
        Assert.DoesNotContain("csharp", MappingExpressionAbiV3.CompilerProfileId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LanguageWhitelistIsAnExactVersionedSnapshot()
    {
        Assert.Equal(
            [
                "CurrentValue", "EffectiveRootNote", "EventInstrumentRootNote", "GateLength",
                "LogicalParameterValue", "PitchDelta", "ProjectTick", "SegmentLocalTick",
                "SubVoiceEffectiveRootNote", "SubVoiceIndex", "TargetOriginalValue", "TemplateNote",
                "TemplateTick", "TemplateVelocity", "TriggerNote", "TriggerVelocity"
            ],
            MappingExpressionLanguageV3.NumericContextFields.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["CurrentEventKind", "CurrentParameter"],
            MappingExpressionLanguageV3.EnumContextFields.Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "Abs", "Acos", "Acosh", "Asin", "Asinh", "Atan", "Atan2", "Atanh",
                "Cbrt", "Ceiling", "Clamp", "CopySign", "Cos", "Cosh", "Exp", "Floor",
                "IEEERemainder", "Log", "Log10", "Log2", "Max", "MaxMagnitude", "Min",
                "MinMagnitude", "Pow", "Round", "Sin", "Sinh", "Sqrt", "Tan", "Tanh",
                "Truncate"
            ],
            MappingExpressionLanguageV3.MathMethodNames.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["E", "PI", "Tau"],
            MappingExpressionLanguageV3.MathConstantNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CompilerCachesExactExpressionAndEvaluatesApprovedNumericLanguage()
    {
        MidoraProject project = new(480);
        CSharpMappingFunction function = Function(
            project,
            "Math.Clamp(value * Math.Pow(context.TriggerVelocity / 127.0, 2), 0, 127)");
        using MappingExpressionCompiler compiler = new();

        MappingExpressionCompiler.MappingDelegate first = compiler.GetOrCompile(function);
        MappingExpressionCompiler.MappingDelegate second = compiler.GetOrCompile(function);
        MappingContextV2 context = Context(triggerVelocity: 127);

        Assert.Same(first, second);
        Assert.Equal(1, compiler.CompilationCount);
        Assert.Equal(32, first(32, in context), 8);
        Assert.Equal([nameof(MappingContextV2.TriggerVelocity)],
            compiler.GetReferencedContextFields(function).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ApprovedMathMethodsAndConstantsHaveEquivalentImplicitAndQualifiedForms()
    {
        MidoraProject project = new(480);
        CSharpMappingFunction implicitForm = Function(
            project,
            "Clamp(value + Sin(PI / 2) * Pow(2, 3) + E - E + Tau - Tau, 0, 127)");
        CSharpMappingFunction qualifiedForm = Function(
            project,
            "Math.Clamp(value + Math.Sin(Math.PI / 2) * Math.Pow(2, 3) + Math.E - Math.E + Math.Tau - Math.Tau, 0, 127)");
        using MappingExpressionCompiler compiler = new();
        MappingContextV2 context = Context();

        double implicitResult = compiler.GetOrCompile(implicitForm)(20, in context);
        double qualifiedResult = compiler.GetOrCompile(qualifiedForm)(20, in context);

        Assert.Equal(28, implicitResult, 8);
        Assert.Equal(qualifiedResult, implicitResult, 8);
    }

    [Fact]
    public void ConditionalAndApprovedEnumsAreSupported()
    {
        MidoraProject project = new(480);
        CSharpMappingFunction function = Function(
            project,
            "context.CurrentEventKind == MappingEventKindV2.ControlChange ? value + 2 : value - 2");
        using MappingExpressionCompiler compiler = new();
        MappingExpressionCompiler.MappingDelegate mapping = compiler.GetOrCompile(function);
        MappingContextV2 control = Context() with
        {
            CurrentEventKind = MappingEventKindV2.ControlChange
        };
        MappingContextV2 note = Context() with { CurrentEventKind = MappingEventKindV2.Note };

        Assert.Equal(22, mapping(20, in control));
        Assert.Equal(18, mapping(20, in note));
    }

    [Theory]
    [InlineData("return value;")]
    [InlineData("while (true) { }")]
    [InlineData("System.IO.File.Delete(\"x\")")]
    [InlineData("System.Diagnostics.Process.Start(\"x\")")]
    [InlineData("new object()")]
    [InlineData("typeof(Math)")]
    [InlineData("default(double)")]
    [InlineData("checked(value + 1)")]
    [InlineData("(() => value)()")]
    [InlineData("value = 12")]
    [InlineData("context.EventInstrumentName == null ? value : 0")]
    [InlineData("context.EventInstrumentId.Value")]
    [InlineData("Math.GetType()")]
    [InlineData("Math.Sign(value)")]
    [InlineData("Sign(value)")]
    public void FreeCSharpAndNonWhitelistedCapabilitiesAreRejected(string expression)
    {
        MidoraProject project = new(480);
        using MappingExpressionCompiler compiler = new();

        MappingException error = Assert.Throws<MappingException>(() =>
            compiler.GetOrCompile(Function(project, expression)));

        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public void FreeCSharpAbiV2IsRecognizedButNeverExecuted()
    {
        MidoraProject project = new(480);
        CSharpMappingFunction function = Function(project, "System.IO.File.Delete(\"x\")");
        function.AbiVersion = MappingAbiV2.Version;
        using MappingExpressionCompiler compiler = new();

        MappingException error = Assert.Throws<MappingException>(() => compiler.GetOrCompile(function));

        Assert.Contains("Free C# Mapping Functions are not executed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLengthLineAndSyntaxComplexityAreBoundedBeforeExecution()
    {
        MidoraProject project = new(480);
        using MappingExpressionCompiler compiler = new();

        Assert.Contains("single line", Assert.Throws<MappingException>(() =>
            compiler.GetOrCompile(Function(project, "value +\n1"))).Message, StringComparison.Ordinal);
        Assert.Contains("Unicode-scalar limit", Assert.Throws<MappingException>(() =>
            compiler.GetOrCompile(Function(
                project,
                "value" + new string(' ', MappingExpressionAbiV3.MaximumSourceLength)))).Message,
            StringComparison.Ordinal);
        string wide = string.Join(" + ", Enumerable.Repeat("value", 300));
        Assert.Contains("syntax-node limit", Assert.Throws<MappingException>(() =>
            compiler.GetOrCompile(Function(project, wide))).Message, StringComparison.Ordinal);
        string deep = new string('(', MappingExpressionAbiV3.MaximumSyntaxDepth + 2)
            + "value"
            + new string(')', MappingExpressionAbiV3.MaximumSyntaxDepth + 2);
        Assert.Contains("nesting-depth", Assert.Throws<MappingException>(() =>
            compiler.GetOrCompile(Function(project, deep))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFiniteResultFailsTheFormalMappingPipeline()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction function = Function(fixture.Project, "value / 0");
        fixture.Instrument.MappingFunctions.Add(function);
        TemplateEvent controller = TemplateEvent.ControlChange(fixture.Project, 0, 1, 20);
        controller.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        fixture.Voice.Events.Add(controller);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Source.MappingFunctionId == function.Id
            && diagnostic.Message.Contains("NaN or Infinity", StringComparison.Ordinal));
    }

    [Fact]
    public void FormalCompilationRequiresThePersistedDependencySetToMatchInference()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction function = Function(fixture.Project, "context.ProjectTick");
        fixture.Instrument.MappingFunctions.Add(function);
        TemplateEvent controller = TemplateEvent.ControlChange(fixture.Project, 0, 1, 20);
        controller.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        fixture.Voice.Events.Add(controller);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult stale = compiler.CompileFull(fixture.Project);
        function.DeclaredContextFields.Add(nameof(MappingContextV2.ProjectTick));
        CanonicalCompiledResult synchronized = compiler.CompileFull(fixture.Project);

        Assert.False(stale.IsConsumable);
        Assert.Contains(stale.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("dependencies do not match", StringComparison.Ordinal));
        Assert.True(synchronized.IsConsumable, string.Join(
            Environment.NewLine,
            synchronized.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void FunctionReceivesTheCurrentAccumulatedValueInValueAndContext()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction function = Function(
            fixture.Project,
            "value == context.CurrentValue ? value : -1");
        function.DeclaredContextFields.Add(nameof(MappingContextV2.CurrentValue));
        fixture.Instrument.MappingFunctions.Add(function);
        TemplateEvent controller = TemplateEvent.ControlChange(fixture.Project, 0, 1, 20);
        controller.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Add,
            Constant = 5
        });
        controller.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        fixture.Voice.Events.Add(controller);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Contains(result.Events.ToArray(), midiEvent =>
            midiEvent.Message.MessageType == Midora.Midi.MidiMessageType.ControlChange
            && midiEvent.Message.Byte1 == 1
            && midiEvent.Message.Byte2 == 25);
    }

    [Fact]
    public void CacheEvictionDropsObsoleteExpressionRevision()
    {
        MidoraProject project = new(480);
        CSharpMappingFunction function = Function(project, "value");
        using MappingExpressionCompiler compiler = new();
        compiler.SynchronizeFunctions([function]);
        _ = compiler.GetOrCompile(function);

        function.Body = "value + 1";
        compiler.SynchronizeFunctions([function]);

        Assert.Equal(0, compiler.CachedEntryCount);
    }

    private static CSharpMappingFunction Function(MidoraProject project, string expression) => new(project)
    {
        Name = "mapping",
        Body = expression
    };

    private static MappingContextV2 Context(int triggerVelocity = 100) => new(
        CurrentValue: 0,
        TriggerNote: 60,
        TriggerVelocity: triggerVelocity,
        GateLength: 480,
        PitchDelta: 0,
        TemplateTick: 0,
        ProjectTick: 0,
        TemplateNote: 60,
        TemplateVelocity: 100);
}
