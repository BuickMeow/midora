using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Midora.Domain;
using Midora.Mapping.Contract.V1;

namespace Midora.Compiler;

internal sealed class MappingException : Exception
{
    public MappingException(string message) : base(message)
    {
    }

    public MappingException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public MidoraId? MappingStepId { get; set; }
    public MidoraId? MappingFunctionId { get; set; }
    public MidoraId? LogicalParameterId { get; set; }
    public MidoraId? EnvelopeId { get; set; }
}

internal sealed class MappingEngine : IDisposable
{
    private readonly CSharpMappingCompiler _csharp = new();

    public void SynchronizeFunctions(IEnumerable<CSharpMappingFunction> functions) =>
        _csharp.SynchronizeFunctions(functions);

    public void ClearCache() => _csharp.Clear();

    public void Dispose() => _csharp.Dispose();

    public string? ValidateFunction(CSharpMappingFunction function)
    {
        try
        {
            _ = _csharp.GetOrCompile(function);
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
        in MappingContextV1 context,
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
            if (!step.IsEnabled)
            {
                continue;
            }
            lastAppliedStep = step;
            try
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
                    MappingOperation.CustomCSharp => EvaluateCSharp(step, current, context, functions),
                    _ => throw new MappingException($"Unknown mapping operation {step.Operation}.")
                };

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

    private double EvaluateCSharp(
        ValueMappingStep step,
        double current,
        in MappingContextV1 context,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions)
    {
        try
        {
            if (!step.MappingFunctionId.HasValue
                || !functions.TryGetValue(step.MappingFunctionId.Value, out CSharpMappingFunction? function))
            {
                throw new MappingException($"Mapping Function '{step.MappingFunctionId}' is unavailable.");
            }
            MappingContextV1 invocationContext = context with { CurrentValue = current };
            return _csharp.GetOrCompile(function)(current, in invocationContext);
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
        in MappingContextV1 context,
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

internal sealed class CSharpMappingCompiler : IDisposable
{
    internal delegate double MappingDelegate(double value, in MappingContextV1 context);

    internal const string ReferencePackRelativeDirectory = "mapping-reference-pack/v1";
    internal const string GeneratedTypeName = "MidoraGeneratedMappingV1";
    internal static readonly LanguageVersion FixedLanguageVersion = LanguageVersion.CSharp14;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ContextFieldNames = typeof(MappingContextV1)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Select(property => property.Name)
        .ToHashSet(StringComparer.Ordinal);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(
        ResolveReferences, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<CacheKey, CacheEntry> _cache = [];
    private bool _disposed;

    internal int CompilationCount { get; private set; }
    internal int CachedEntryCount => _cache.Count;

    public MappingDelegate GetOrCompile(CSharpMappingFunction function)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateDefinition(function);
        CacheKey key = CreateKey(function.AbiVersion, function.Body);
        if (!_cache.TryGetValue(key, out CacheEntry? entry))
        {
            entry = Compile(key, function.Body);
            _cache.Add(key, entry);
            CompilationCount++;
        }
        if (entry.Error is not null)
        {
            throw new MappingException(entry.Error);
        }
        return entry.Delegate!;
    }

    public void SynchronizeFunctions(IEnumerable<CSharpMappingFunction> functions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(functions);
        HashSet<CacheKey> live = [];
        foreach (CSharpMappingFunction function in functions.Where(function => function is not null))
        {
            try
            {
                live.Add(CreateKey(function.AbiVersion, function.Body ?? string.Empty));
            }
            catch (EncoderFallbackException)
            {
                // Invalid source cannot own a compiled cache entry; GetOrCompile emits its diagnostic.
            }
        }
        foreach (CacheKey stale in _cache.Keys.Where(key => !live.Contains(key)).ToArray())
        {
            CacheEntry entry = _cache[stale];
            _cache.Remove(stale);
            entry.Dispose();
        }
    }

    public void Clear()
    {
        foreach (CacheEntry entry in _cache.Values)
        {
            entry.Dispose();
        }
        _cache.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Clear();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~CSharpMappingCompiler()
    {
        try
        {
            Clear();
        }
        catch
        {
            // A finalizer is only a leak-prevention fallback and must never escape.
        }
    }

    internal WeakReference? GetLoadContextWeakReference(CSharpMappingFunction function)
    {
        CacheKey key = CreateKey(function.AbiVersion, function.Body);
        return _cache.TryGetValue(key, out CacheEntry? entry) ? entry.LoadContextWeakReference : null;
    }

    private static void ValidateDefinition(CSharpMappingFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (function.AbiVersion != MappingAbiV1.Version)
        {
            throw new MappingException(
                $"Unsupported C# Mapping ABI version {function.AbiVersion}; expected {MappingAbiV1.Version}.");
        }
        if (string.IsNullOrWhiteSpace(function.Body))
        {
            throw new MappingException("C# Mapping function body cannot be empty.");
        }
        try
        {
            _ = StrictUtf8.GetByteCount(function.Body);
        }
        catch (EncoderFallbackException exception)
        {
            throw new MappingException("C# Mapping function body must be valid Unicode with an exact UTF-8 encoding.", exception);
        }
        string[] invalidFields = function.DeclaredContextFields
            .Where(field => !ContextFieldNames.Contains(field))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (invalidFields.Length != 0)
        {
            throw new MappingException(
                $"C# Mapping declares unknown ABI v1 context fields: {string.Join(", ", invalidFields)}.");
        }
    }

    private static CacheKey CreateKey(int abiVersion, string body)
    {
        byte[] input = StrictUtf8.GetBytes(body);
        string bodyHash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return new(abiVersion, MappingAbiV1.CompilerProfileId, bodyHash);
    }

    private static CacheEntry Compile(CacheKey key, string body)
    {
        string source = $$"""
            #nullable enable
            using System;
            using Midora.Mapping.Contract.V1;

            public static class {{GeneratedTypeName}}
            {
                public static double Transform(double value, in MappingContextV1 context)
                {
            #line 1 "mapping-function-v1.cs"
            {{body}}
            #line default
                }
            }
            """;
        CSharpParseOptions parseOptions = new(
            FixedLanguageVersion,
            DocumentationMode.None,
            SourceCodeKind.Regular);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8),
            parseOptions,
            "mapping-function-v1.cs");
        CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();
        if (root.Members.Count != 1
            || root.Members[0] is not ClassDeclarationSyntax generatedClass
            || generatedClass.Identifier.ValueText != GeneratedTypeName
            || generatedClass.Members.Count != 1
            || generatedClass.Members[0] is not MethodDeclarationSyntax)
        {
            return CacheEntry.Failure(
                "C# Mapping ABI v1 source must be a function body and cannot replace the generated wrapper.");
        }

        string assemblyName = $"Midora.Mapping.Generated.V1.{key.SourceHash}";
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                checkOverflow: false,
                allowUnsafe: true,
                platform: Platform.X64,
                warningLevel: 4,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true,
                concurrentBuild: false));
        using MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);
        if (!result.Success)
        {
            string text = string.Join(Environment.NewLine, result.Diagnostics
                .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
                .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
                .Select(diagnostic => diagnostic.ToString()));
            return CacheEntry.Failure($"C# Mapping compilation failed: {text}");
        }

        stream.Position = 0;
        MappingAssemblyLoadContext loadContext = new(assemblyName);
        try
        {
            Assembly assembly = loadContext.LoadFromStream(stream);
            MethodInfo method = assembly.GetType(GeneratedTypeName, throwOnError: true)!
                .GetMethod("Transform", BindingFlags.Public | BindingFlags.Static)!;
            return CacheEntry.Success(method.CreateDelegate<MappingDelegate>(), loadContext);
        }
        catch (Exception exception)
        {
            loadContext.Unload();
            return CacheEntry.Failure($"C# Mapping assembly load failed: {exception.Message}");
        }
    }

    private static IReadOnlyList<MetadataReference> ResolveReferences()
    {
        string directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            ReferencePackRelativeDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (!Directory.Exists(directory))
        {
            throw new MappingException($"C# Mapping ABI v1 reference pack is missing: {directory}.");
        }

        string[] paths = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(path => IsAllowedFrameworkReference(Path.GetFileName(path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!paths.Any(path => string.Equals(Path.GetFileName(path), "System.Runtime.dll", StringComparison.Ordinal)))
        {
            throw new MappingException($"C# Mapping ABI v1 reference pack is incomplete: {directory}.");
        }

        List<MetadataReference> references = paths
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        string contractPath = typeof(MappingContextV1).Assembly.Location;
        if (string.IsNullOrWhiteSpace(contractPath) || !File.Exists(contractPath))
        {
            throw new MappingException("C# Mapping ABI v1 contract assembly has no usable file location.");
        }
        references.Add(MetadataReference.CreateFromFile(contractPath));
        return references;
    }

    private static bool IsAllowedFrameworkReference(string fileName) =>
        fileName is "mscorlib.dll" or "netstandard.dll" or "Microsoft.CSharp.dll"
            or "Microsoft.VisualBasic.dll" or "Microsoft.VisualBasic.Core.dll"
        || fileName.StartsWith("System.", StringComparison.Ordinal)
        || fileName.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);

    private readonly record struct CacheKey(int AbiVersion, string CompilerProfile, string SourceHash);

    private sealed class CacheEntry : IDisposable
    {
        private MappingDelegate? _delegate;
        private MappingAssemblyLoadContext? _loadContext;

        private CacheEntry(MappingDelegate? mappingDelegate, MappingAssemblyLoadContext? loadContext, string? error)
        {
            _delegate = mappingDelegate;
            _loadContext = loadContext;
            Error = error;
            LoadContextWeakReference = loadContext is null ? null : new WeakReference(loadContext);
        }

        public MappingDelegate? Delegate => _delegate;
        public string? Error { get; }
        public WeakReference? LoadContextWeakReference { get; }

        public static CacheEntry Success(MappingDelegate mappingDelegate, MappingAssemblyLoadContext loadContext) =>
            new(mappingDelegate, loadContext, null);

        public static CacheEntry Failure(string error) => new(null, null, error);

        public void Dispose()
        {
            _delegate = null;
            MappingAssemblyLoadContext? loadContext = _loadContext;
            _loadContext = null;
            loadContext?.Unload();
        }
    }

    private sealed class MappingAssemblyLoadContext : AssemblyLoadContext
    {
        public MappingAssemblyLoadContext(string name) : base(name, isCollectible: true)
        {
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            Assembly contract = typeof(MappingContextV1).Assembly;
            return AssemblyName.ReferenceMatchesDefinition(assemblyName, contract.GetName()) ? contract : null;
        }
    }
}
