using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Midora.Domain;
using Midora.Mapping.Contract.V1;

namespace Midora.Compiler.Tests;

public sealed class CSharpMappingAbiV1Tests
{
    [Fact]
    public void ContractPublicSurfaceIsVersionedReadonlyAndDomainIndependent()
    {
        Assert.Equal(1, MappingAbiV1.Version);
        Assert.Equal("10.0.10", MappingAbiV1.NetCoreReferencePackVersion);
        Assert.Equal("midora-csharp14-net10.0.10-roslyn5.3-v1", MappingAbiV1.CompilerProfileId);
        Assert.True(typeof(MappingContextV1).IsValueType);
        Assert.NotNull(typeof(MappingContextV1).GetCustomAttribute<IsReadOnlyAttribute>());

        string[] exportedTypes = typeof(MappingContextV1).Assembly.GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "Midora.Mapping.Contract.V1.MappingAbiV1",
            "Midora.Mapping.Contract.V1.MappingContextV1",
            "Midora.Mapping.Contract.V1.MappingEventKindV1",
            "Midora.Mapping.Contract.V1.MappingStableIdV1",
            "Midora.Mapping.Contract.V1.MappingTargetParameterV1"
        ], exportedTypes);
        Assert.DoesNotContain(typeof(MappingContextV1).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("Midora.", StringComparison.Ordinal) == true);

        PropertyInfo[] properties = typeof(MappingContextV1).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Assert.Equal(27, properties.Length);
        Assert.Equal(
        [
            "CurrentEventId", "CurrentEventKind", "CurrentParameter", "CurrentValue", "EffectiveRootNote",
            "EventInstrumentId", "EventInstrumentName", "EventInstrumentRootNote", "GateLength",
            "LogicalParameterId", "LogicalParameterName", "LogicalParameterValue", "PitchDelta", "ProjectTick",
            "SegmentId", "SegmentLocalTick", "SubVoiceEffectiveRootNote", "SubVoiceId", "SubVoiceIndex",
            "SubVoiceName", "TargetOriginalValue", "TemplateNote", "TemplateTick", "TemplateVelocity", "TrackId",
            "TriggerNote", "TriggerVelocity"
        ], properties.Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(properties, property =>
        {
            Assert.NotNull(property.GetMethod);
            Assert.Contains(typeof(IsExternalInit), property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
            Assert.DoesNotContain("Midora.Domain", property.PropertyType.FullName ?? string.Empty, StringComparison.Ordinal);
        });

        Assert.Equal(0, (int)MappingEventKindV1.Unknown);
        Assert.Equal(8, (int)MappingEventKindV1.PitchBendRange);
        Assert.Equal(0, (int)MappingTargetParameterV1.Unknown);
        Assert.Equal(4, (int)MappingTargetParameterV1.LogicalParameterOutput);
        Assert.Equal("0123456789abcdeffedcba9876543210",
            new MappingStableIdV1(0x0123456789abcdef, 0xfedcba9876543210).ToString());
    }

    [Fact]
    public void CompilerProfileIsPinnedAndCacheReusesTheExactSourceRevision()
    {
        MidoraProject project = new(480);
        CSharpMappingFunction function = Function(project,
            "return value + context.TriggerVelocity;");
        function.DeclaredContextFields.Add(nameof(MappingContextV1.TriggerVelocity));
        using CSharpMappingCompiler compiler = new();
        compiler.SynchronizeFunctions([function]);

        CSharpMappingCompiler.MappingDelegate first = compiler.GetOrCompile(function);
        CSharpMappingCompiler.MappingDelegate second = compiler.GetOrCompile(function);
        MappingContextV1 context = new(2, 60, 7, 120, 0, 0, 0, 60, 100);

        Assert.Same(first, second);
        Assert.Equal(9, first(2, in context));
        Assert.Equal(1, compiler.CompilationCount);
        Assert.Equal(1, compiler.CachedEntryCount);
        Assert.Equal(LanguageVersion.CSharp14, CSharpMappingCompiler.FixedLanguageVersion);
        Assert.Equal(new Version(5, 3, 0, 0), typeof(CSharpCompilation).Assembly.GetName().Version);
        string bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(function.Body))).ToLowerInvariant();
        Assert.Equal($"Midora.Mapping.Generated.V1.{bodyHash}", first.Method.Module.Assembly.GetName().Name);
        Assert.True(AssemblyLoadContext.GetLoadContext(first.Method.Module.Assembly)!.IsCollectible);
        ParameterInfo[] delegateParameters = typeof(CSharpMappingCompiler.MappingDelegate).GetMethod("Invoke")!.GetParameters();
        Assert.Equal(typeof(MappingContextV1).MakeByRefType(), delegateParameters[1].ParameterType);
        Assert.True(delegateParameters[1].IsIn);
        Assert.True(first.Method.GetParameters()[1].IsIn);

        function.DeclaredContextFields.Add(nameof(MappingContextV1.ProjectTick));
        Assert.Same(first, compiler.GetOrCompile(function));
        Assert.Equal(1, compiler.CompilationCount);
    }

    [Fact]
    public void CompilerAllowsNet10CoreApisButNotApplicationOrThirdPartyReferences()
    {
        MidoraProject project = new(480);
        using CSharpMappingCompiler compiler = new();
        CSharpMappingFunction coreApi = Function(project,
            "return System.IO.Path.GetFileName(\"a/b.txt\").Length;");
        compiler.SynchronizeFunctions([coreApi]);

        CSharpMappingCompiler.MappingDelegate mapping = compiler.GetOrCompile(coreApi);
        MappingContextV1 context = default;
        Assert.Equal(5, mapping(0, in context));

        CSharpMappingFunction domain = Function(project,
            "return typeof(Midora.Domain.MidoraProject).Name.Length;");
        CSharpMappingFunction roslyn = Function(project,
            "return typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Name.Length;");
        CSharpMappingFunction windowsDesktop = Function(project,
            "return typeof(System.Windows.Window).Name.Length;");
        Assert.Contains("compilation failed", Assert.Throws<MappingException>(() => compiler.GetOrCompile(domain)).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compilation failed", Assert.Throws<MappingException>(() => compiler.GetOrCompile(roslyn)).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compilation failed", Assert.Throws<MappingException>(() => compiler.GetOrCompile(windowsDesktop)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompilerAllowsUnsafeCSharpAsRequiredByTheUnsandboxedFreeCSharpProfile()
    {
        MidoraProject project = new(480);
        CSharpMappingFunction function = Function(project,
            "unsafe { double copy = value; double* pointer = &copy; return *pointer; }");
        using CSharpMappingCompiler compiler = new();
        compiler.SynchronizeFunctions([function]);

        CSharpMappingCompiler.MappingDelegate mapping = compiler.GetOrCompile(function);
        MappingContextV1 context = default;

        Assert.Equal(12.5, mapping(12.5, in context));
    }

    [Fact]
    public void CompilerRejectsUnknownAbiFieldsAndWrapperReplacement()
    {
        MidoraProject project = new(480);
        using CSharpMappingCompiler compiler = new();
        CSharpMappingFunction unknownAbi = Function(project, "return value;");
        unknownAbi.AbiVersion = 2;
        Assert.Contains("ABI version 2", Assert.Throws<MappingException>(() => compiler.GetOrCompile(unknownAbi)).Message,
            StringComparison.Ordinal);

        CSharpMappingFunction unknownField = Function(project, "return value;");
        unknownField.DeclaredContextFields.Add("Project");
        Assert.Contains("unknown ABI v1 context fields",
            Assert.Throws<MappingException>(() => compiler.GetOrCompile(unknownField)).Message,
            StringComparison.Ordinal);

        CSharpMappingFunction escape = Function(project,
            "return value; } public static double Escape() => 1; public static double Continue(double value");
        Assert.Contains("function body",
            Assert.Throws<MappingException>(() => compiler.GetOrCompile(escape)).Message,
            StringComparison.OrdinalIgnoreCase);

        CSharpMappingFunction invalidUnicode = Function(project, "return value; // \ud800");
        Assert.Contains("valid Unicode",
            Assert.Throws<MappingException>(() => compiler.GetOrCompile(invalidUnicode)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownAbiIsWarningWhenUnusedAndErrorWhenUsed()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction function = Function(fixture.Project, "return value;");
        function.AbiVersion = 2;
        fixture.Instrument.MappingFunctions.Add(function);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult unused = compiler.CompileFull(fixture.Project);

        Assert.True(unused.IsConsumable);
        Assert.Contains(unused.Diagnostics, diagnostic =>
            diagnostic.Code == "MIDORA2104"
            && diagnostic.Severity == DiagnosticSeverity.Warning
            && diagnostic.Source.MappingFunctionId == function.Id);

        TemplateEvent value = TemplateEvent.ControlChange(fixture.Project, 0, 1, 20);
        value.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        fixture.Voice.Events.Add(value);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult used = compiler.CompileFull(fixture.Project);

        Assert.False(used.IsConsumable);
        Assert.Contains(used.Diagnostics, diagnostic =>
            diagnostic.Code == "MIDORA2103"
            && diagnostic.Severity == DiagnosticSeverity.Error
            && diagnostic.Source.MappingFunctionId == function.Id);
    }

    [Fact]
    public void MappingFunctionNamesUseTrimmedCaseInsensitiveUniquenessWithoutRewritingSourceName()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction first = Function(fixture.Project, "return value;");
        first.Name = "  Human Name  ";
        fixture.Instrument.MappingFunctions.Add(first);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult one = compiler.CompileFull(fixture.Project);

        Assert.DoesNotContain(one.Diagnostics, diagnostic => diagnostic.Code == "MIDORA1273");
        Assert.Equal("  Human Name  ", first.Name);

        CSharpMappingFunction duplicate = Function(fixture.Project, "return value + 1;");
        duplicate.Name = "human name";
        fixture.Instrument.MappingFunctions.Add(duplicate);
        CanonicalCompiledResult two = compiler.CompileFull(fixture.Project);

        Assert.False(two.IsConsumable, string.Join(Environment.NewLine,
            two.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Severity}:{diagnostic.Message}")));
        Assert.Contains(two.Diagnostics, diagnostic => diagnostic.Code == "MIDORA1273");
    }

    [Fact]
    public void CustomFunctionReceivesTheCurrentAccumulatedValueInBothArguments()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction function = Function(fixture.Project,
            "return value == context.CurrentValue ? value : -1;");
        fixture.Instrument.MappingFunctions.Add(function);
        TemplateEvent value = TemplateEvent.ControlChange(fixture.Project, 0, 1, 20);
        value.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Add,
            Constant = 5
        });
        value.ValueMappings.Add(new ValueMappingStep(fixture.Project)
        {
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        fixture.Voice.Events.Add(value);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);

        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics.Select(value => value.Message)));
        Assert.Contains(result.Events.ToArray(), midiEvent =>
            midiEvent.Message.MessageType == Midora.Midi.MidiMessageType.ControlChange
            && midiEvent.Message.Byte1 == 1 && midiEvent.Message.Byte2 == 25);
    }

    [Fact]
    public void NoteOnlyContextDeclarationsAreRejectedOutsideNoteTargets()
    {
        var fixture = CompilerTestProject.Create();
        CSharpMappingFunction function = Function(fixture.Project, "return context.TemplateVelocity;");
        function.DeclaredContextFields.Add(nameof(MappingContextV1.TemplateVelocity));
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
            diagnostic.Code == "MIDORA1254" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void LogicalParameterMappingsRejectNoteOnlyGraphSources()
    {
        var fixture = CompilerTestProject.Create();
        LogicalParameterDefinition parameter = new(fixture.Project)
        {
            Name = "parameter",
            Minimum = 0,
            Maximum = 127
        };
        fixture.Instrument.LogicalParameters.Add(parameter);
        LogicalParameterMapping mapping = new(fixture.Project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = fixture.Voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.Steps.Add(new ValueMappingStep(fixture.Project)
        {
            Source = MappingSource.TemplateNote,
            Operation = MappingOperation.Override
        });
        fixture.Instrument.ParameterMappings.Add(mapping);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "MIDORA1252" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void EvictedRevisionReleasesItsCollectibleLoadContext()
    {
        WeakReference loadContext = CompileAndEvictRevision();

        for (int attempt = 0; attempt < 10 && loadContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(loadContext.IsAlive);
    }

    [Fact]
    public void CompilerDisposeReleasesItsCollectibleLoadContext()
    {
        WeakReference loadContext = CompileAndDispose();

        for (int attempt = 0; attempt < 10 && loadContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(loadContext.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompileAndEvictRevision()
    {
        MidoraProject project = new(480);
        CSharpMappingFunction function = Function(project, "return value;");
        using CSharpMappingCompiler compiler = new();
        compiler.SynchronizeFunctions([function]);
        _ = compiler.GetOrCompile(function);
        WeakReference reference = compiler.GetLoadContextWeakReference(function)!;

        function.Body = "return value + 1;";
        compiler.SynchronizeFunctions([function]);
        Assert.Equal(0, compiler.CachedEntryCount);
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompileAndDispose()
    {
        MidoraProject project = new(480);
        CSharpMappingFunction function = Function(project, "return value;");
        CSharpMappingCompiler compiler = new();
        compiler.SynchronizeFunctions([function]);
        _ = compiler.GetOrCompile(function);
        WeakReference reference = compiler.GetLoadContextWeakReference(function)!;

        compiler.Dispose();
        return reference;
    }

    private static CSharpMappingFunction Function(MidoraProject project, string body) => new(project)
    {
        Name = "mapping",
        Body = body
    };
}
