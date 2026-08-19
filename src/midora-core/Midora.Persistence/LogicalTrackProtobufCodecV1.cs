using Google.Protobuf;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;

namespace Midora.Persistence;

internal static class LogicalTrackProtobufCodecV1
{
    public const string ObjectType = "logical-track";

    public static byte[] Serialize(LogicalTrack value)
    {
        ArgumentNullException.ThrowIfNull(value);
        LogicalTrackV1 wire = ToWire(value);
        Validate(wire);
        return StrictProtobufWireV1.SerializeDeterministic(wire);
    }

    public static LogicalTrack Restore(MidoraProject project, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            StrictProtobufWireV1.Validate(bytes, LogicalTrackV1.Descriptor);
            LogicalTrackV1 wire = LogicalTrackV1.Parser.ParseFrom(bytes);
            Validate(wire);
            return FromWire(project, wire);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidDataException("Logical Track protobuf is malformed.", exception);
        }
    }

    private static LogicalTrackV1 ToWire(LogicalTrack value)
    {
        PersistenceValueValidationV1.ValidateShortText(value.Name, "Logical Track name");
        LogicalTrackV1 result = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            ObjectType = ObjectType,
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            Name = value.Name
        };
        result.EventInstrumentId = ProtobufValueCodecV1.ToWire(
            value.EventInstrumentId
            ?? throw new InvalidDataException(
                "A Logical Track must have an Event Instrument parent."));
        if (value.ColorOverride.HasValue)
        {
            result.ColorOverride = ProtobufValueCodecV1.ToWire(value.ColorOverride.Value);
        }
        result.Segments.Add(value.Segments.Select(ToWire));
        return result;
    }

    private static LogicalTrack FromWire(MidoraProject project, LogicalTrackV1 value)
    {
        LogicalTrack result = new(project, ProtobufValueCodecV1.FromWire(value.Id, "Logical Track ID"))
        {
            Name = value.Name,
            EventInstrumentId = ProtobufValueCodecV1.FromWire(
                value.EventInstrumentId,
                "Logical Track Event Instrument ID"),
            ColorOverride = value.ColorOverride is null
                ? null
                : ProtobufValueCodecV1.FromWire(value.ColorOverride, "Logical Track color override")
        };
        result.Segments.AddRange(value.Segments.Select(item => FromWire(project, item)));
        return result;
    }

    private static SegmentV1 ToWire(Segment value)
    {
        SegmentV1 result = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            ProjectStartTick = value.ProjectStartTick,
            LengthTicks = value.LengthTicks,
            ContentOffsetTick = value.ContentOffsetTick
        };
        result.Notes.Add(value.Notes.Select(ToWire));
        result.ParameterLanes.Add(value.ParameterLanes.Select(ToWire));
        return result;
    }

    private static Segment FromWire(MidoraProject project, SegmentV1 value)
    {
        Segment result = new(project, ProtobufValueCodecV1.FromWire(value.Id, "Segment ID"))
        {
            ProjectStartTick = value.ProjectStartTick,
            LengthTicks = value.LengthTicks,
            ContentOffsetTick = value.ContentOffsetTick
        };
        result.Notes.AddRange(value.Notes.Select(item => FromWire(project, item)));
        result.ParameterLanes.AddRange(value.ParameterLanes.Select(item => FromWire(project, item)));
        return result;
    }

    private static LogicalNoteV1 ToWire(LogicalNote value) => new()
    {
        Id = ProtobufValueCodecV1.ToWire(value.Id),
        StartTick = value.StartTick,
        LengthTicks = value.LengthTicks,
        Note = value.Note,
        Velocity = value.Velocity
    };

    private static LogicalNote FromWire(MidoraProject project, LogicalNoteV1 value) => new(
        project,
        ProtobufValueCodecV1.FromWire(value.Id, "Logical Note ID"))
    {
        StartTick = value.StartTick,
        LengthTicks = value.LengthTicks,
        Note = value.Note,
        Velocity = value.Velocity
    };

    private static LogicalParameterLaneV1 ToWire(LogicalParameterLane value)
    {
        LogicalParameterLaneV1 result = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            ParameterId = ProtobufValueCodecV1.ToWire(value.ParameterId)
        };
        result.Points.Add(value.Points.Select(EventInstrumentProtobufCodecV1.ToWire));
        return result;
    }

    private static LogicalParameterLane FromWire(MidoraProject project, LogicalParameterLaneV1 value)
    {
        LogicalParameterLane result = new(
            project,
            ProtobufValueCodecV1.FromWire(value.Id, "Logical Parameter Lane ID"))
        {
            ParameterId = ProtobufValueCodecV1.FromWire(value.ParameterId, "Logical Parameter Lane parameter ID")
        };
        result.Points.AddRange(value.Points.Select(item => EventInstrumentProtobufCodecV1.FromWire(project, item)));
        return result;
    }

    private static void Validate(LogicalTrackV1 value)
    {
        ProtobufValueCodecV1.Require(value.HasSchemaVersion, "Logical Track schemaVersion");
        ProtobufValueCodecV1.Require(value.HasObjectType, "Logical Track objectType");
        ProtobufValueCodecV1.Require(value.HasName, "Logical Track name");
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion || value.ObjectType != ObjectType)
        {
            throw new ProtobufObjectHeaderExceptionV1(
                "Logical Track schemaVersion or objectType is inconsistent with its manifest identity.");
        }
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Logical Track ID");
        PersistenceValueValidationV1.ValidateShortText(value.Name, "Logical Track name");
        _ = ProtobufValueCodecV1.FromWire(
            value.EventInstrumentId,
            "Logical Track Event Instrument ID");
        if (value.ColorOverride is not null)
        {
            _ = ProtobufValueCodecV1.FromWire(value.ColorOverride, "Logical Track color override");
        }
        foreach (SegmentV1 segment in value.Segments) Validate(segment);
    }

    private static void Validate(SegmentV1 value)
    {
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Segment ID");
        ProtobufValueCodecV1.Require(value.HasProjectStartTick, "Segment projectStartTick");
        ProtobufValueCodecV1.Require(value.HasLengthTicks, "Segment lengthTicks");
        ProtobufValueCodecV1.Require(value.HasContentOffsetTick, "Segment contentOffsetTick");
        foreach (LogicalNoteV1 note in value.Notes)
        {
            _ = ProtobufValueCodecV1.FromWire(note.Id, "Logical Note ID");
            ProtobufValueCodecV1.Require(note.HasStartTick, "Logical Note startTick");
            ProtobufValueCodecV1.Require(note.HasLengthTicks, "Logical Note lengthTicks");
            ProtobufValueCodecV1.Require(note.HasNote, "Logical Note note");
            ProtobufValueCodecV1.Require(note.HasVelocity, "Logical Note velocity");
        }
        foreach (LogicalParameterLaneV1 lane in value.ParameterLanes)
        {
            _ = ProtobufValueCodecV1.FromWire(lane.Id, "Logical Parameter Lane ID");
            _ = ProtobufValueCodecV1.FromWire(lane.ParameterId, "Logical Parameter Lane parameter ID");
            foreach (CurvePointV1 point in lane.Points) EventInstrumentProtobufCodecV1.Validate(point);
        }
    }
}
