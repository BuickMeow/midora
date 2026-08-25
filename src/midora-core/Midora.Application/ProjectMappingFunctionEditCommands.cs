using Midora.Domain;
using Midora.Mapping.Contract.V2;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateMappingFunction(
        MidoraId eventInstrumentId,
        MidoraId mappingFunctionId,
        string name,
        string body,
        IEnumerable<string> declaredContextFields)
    {
        ArgumentNullException.ThrowIfNull(declaredContextFields);
        string[] frozenFields = declaredContextFields.ToArray();
        return Command("Change mapping function", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            CSharpMappingFunction function = FindMappingFunction(
                instrument,
                mappingFunctionId);
            string normalizedName = NormalizeUniqueMappingFunctionName(
                instrument,
                mappingFunctionId,
                name);
            string validatedBody = ProjectTextRules.ValidateMappingBody(body, nameof(body));
            string[] normalizedFields = NormalizeContextFields(frozenFields);
            MappingFunctionValue old = CaptureMappingFunction(function);
            MappingFunctionValue replacement = new(
                normalizedName,
                validatedBody,
                MappingExpressionAbiV3.Version,
                normalizedFields);
            return Prepared(
                !MappingFunctionValuesEqual(old, replacement),
                EventInstrumentChange(eventInstrumentId),
                _ => SetMappingFunction(function, replacement),
                _ => SetMappingFunction(function, old));
        });
    }

    public static IProjectEditCommand DeleteMappingFunction(
        MidoraId eventInstrumentId,
        MidoraId mappingFunctionId,
        bool referencedDeletionConfirmed) =>
        Command("Delete mapping function", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            CSharpMappingFunction function = FindMappingFunction(
                instrument,
                mappingFunctionId);
            bool isReferenced = EnumerateMappingSteps(instrument)
                .Any(value => value.MappingFunctionId == mappingFunctionId);
            if (isReferenced && !referencedDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a referenced Mapping Function requires explicit confirmation.");
            }
            int originalIndex = instrument.MappingFunctions.IndexOf(function);
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ => RemoveRequired(
                    instrument.MappingFunctions,
                    function,
                    "Mapping Function"),
                _ => InsertAt(
                    instrument.MappingFunctions,
                    originalIndex,
                    function,
                    "Mapping Function"));
        });

    private static CSharpMappingFunction FindMappingFunction(
        EventInstrument instrument,
        MidoraId mappingFunctionId) =>
        instrument.MappingFunctions.SingleOrDefault(value => value.Id == mappingFunctionId)
        ?? throw new ArgumentOutOfRangeException(nameof(mappingFunctionId));

    private static string NormalizeUniqueMappingFunctionName(
        EventInstrument instrument,
        MidoraId mappingFunctionId,
        string name)
    {
        string normalized = ProjectTextRules.NormalizeShortText(
            name,
            allowEmpty: false,
            nameof(name));
        if (instrument.MappingFunctions.Any(value =>
            value.Id != mappingFunctionId
            && string.Equals(value.Name.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "A Mapping Function with the same name already exists in this Event Instrument.");
        }
        return normalized;
    }

    private static string[] NormalizeContextFields(IEnumerable<string> fields)
    {
        HashSet<string> unique = new(StringComparer.Ordinal);
        List<string> result = [];
        foreach (string field in fields)
        {
            string normalized = ProjectTextRules.NormalizeShortText(
                field,
                allowEmpty: false,
                nameof(fields));
            if (!unique.Add(normalized))
            {
                throw new ArgumentException(
                    "Declared Mapping Context fields must be unique.",
                    nameof(fields));
            }
            result.Add(normalized);
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static MappingFunctionValue CaptureMappingFunction(CSharpMappingFunction value) =>
        new(
            value.Name,
            value.Body,
            value.AbiVersion,
            value.DeclaredContextFields.Order(StringComparer.Ordinal).ToArray());

    private static bool MappingFunctionValuesEqual(
        MappingFunctionValue first,
        MappingFunctionValue second) =>
        string.Equals(first.Name, second.Name, StringComparison.Ordinal)
        && string.Equals(first.Body, second.Body, StringComparison.Ordinal)
        && first.AbiVersion == second.AbiVersion
        && first.DeclaredContextFields.SequenceEqual(
            second.DeclaredContextFields,
            StringComparer.Ordinal);

    private static void SetMappingFunction(
        CSharpMappingFunction target,
        MappingFunctionValue value)
    {
        target.Name = value.Name;
        target.Body = value.Body;
        target.AbiVersion = value.AbiVersion;
        target.DeclaredContextFields.Clear();
        target.DeclaredContextFields.UnionWith(value.DeclaredContextFields);
    }

    private sealed record MappingFunctionValue(
        string Name,
        string Body,
        int AbiVersion,
        string[] DeclaredContextFields);
}
