namespace Midora.Domain;

public static class SubVoiceMappingConventions
{
    public static TemplateEventMappingTarget NoteVelocityTarget { get; } =
        TemplateEventMappingTarget.Create(
            TemplateEventKind.Note,
            eventNumber: 0,
            TemplateEventMappingParameter.Value);

    public static bool FollowsInstanceVelocity(SubVoice subVoice)
    {
        ArgumentNullException.ThrowIfNull(subVoice);
        SubVoiceEventMapping? mapping = subVoice.FindEventMapping(NoteVelocityTarget);
        if (mapping is null || !mapping.Steps.IsEnabled)
        {
            return false;
        }

        ValueMappingStep? firstEnabled = mapping.Steps.FirstOrDefault(step => step.IsEnabled);
        return firstEnabled is
        {
            Source: MappingSource.TriggerVelocity,
            Operation: MappingOperation.Override
        };
    }

    public static SubVoiceEventMapping AddDefaultInstanceVelocityMapping(
        MidoraProject project,
        SubVoice subVoice)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(subVoice);
        SubVoiceEventMapping mapping = subVoice.GetOrCreateEventMapping(NoteVelocityTarget);
        if (mapping.Steps.Count != 0)
        {
            throw new InvalidOperationException(
                "The default instance-velocity Mapping can only initialize an empty Note Velocity chain.");
        }

        mapping.Steps.IsEnabled = true;
        mapping.Steps.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.TriggerVelocity,
            Operation = MappingOperation.Override
        });
        return mapping;
    }
}
