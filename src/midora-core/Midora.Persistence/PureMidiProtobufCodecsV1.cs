using Google.Protobuf;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;
using WireChannelMode = Midora.Persistence.Wire.Proto.V1.MidiChannelModeV1;
using WireRoutingMode = Midora.Persistence.Wire.Proto.V1.MidiChannelRootRoutingModeV1;

namespace Midora.Persistence;

internal static class MidiChannelRootProtobufCodecV1
{
    public const string ObjectType = "midi-channel-root";

    public static byte[] Serialize(MidiChannelRoot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        MidiChannelRootV1 wire = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            ObjectType = ObjectType,
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            Name = value.Name,
            RoutingMode = (WireRoutingMode)(int)value.RoutingMode,
            FixedZeroBasedPort = value.FixedZeroBasedPort,
            FixedZeroBasedChannel = value.FixedZeroBasedChannel,
            ChannelMode = (WireChannelMode)(int)value.ChannelMode
        };
        wire.MidiTrackIds.Add(value.MidiTrackIds.Select(ProtobufValueCodecV1.ToWire));
        Validate(wire);
        return StrictProtobufWireV1.SerializeDeterministic(wire);
    }

    public static MidiChannelRoot Restore(MidoraProject project, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            StrictProtobufWireV1.Validate(bytes, MidiChannelRootV1.Descriptor);
            MidiChannelRootV1 wire = MidiChannelRootV1.Parser.ParseFrom(bytes);
            Validate(wire);
            MidiChannelRoot result = new(
                project,
                ProtobufValueCodecV1.FromWire(wire.Id, "MIDI Channel Root ID"))
            {
                Name = wire.Name,
                RoutingMode = (MidiChannelRootRoutingMode)(int)wire.RoutingMode,
                FixedZeroBasedPort = checked((byte)wire.FixedZeroBasedPort),
                FixedZeroBasedChannel = checked((byte)wire.FixedZeroBasedChannel),
                ChannelMode = (MidiChannelMode)(int)wire.ChannelMode
            };
            result.MidiTrackIds.AddRange(wire.MidiTrackIds.Select(
                value => ProtobufValueCodecV1.FromWire(value, "MIDI Channel Root Track ID")));
            return result;
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidDataException("MIDI Channel Root protobuf is malformed.", exception);
        }
    }

    private static void Validate(MidiChannelRootV1 value)
    {
        ProtobufValueCodecV1.Require(value.HasSchemaVersion, "MIDI Channel Root schemaVersion");
        ProtobufValueCodecV1.Require(value.HasObjectType, "MIDI Channel Root objectType");
        ProtobufValueCodecV1.Require(value.HasName, "MIDI Channel Root name");
        ProtobufValueCodecV1.Require(value.HasRoutingMode, "MIDI Channel Root routingMode");
        ProtobufValueCodecV1.Require(value.HasFixedZeroBasedPort, "MIDI Channel Root Port");
        ProtobufValueCodecV1.Require(value.HasFixedZeroBasedChannel, "MIDI Channel Root Channel");
        ProtobufValueCodecV1.Require(value.HasChannelMode, "MIDI Channel Root channelMode");
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion
            || value.ObjectType != ObjectType)
        {
            throw new ProtobufObjectHeaderExceptionV1(
                "MIDI Channel Root schemaVersion or objectType is inconsistent with its manifest identity.");
        }
        _ = ProtobufValueCodecV1.FromWire(value.Id, "MIDI Channel Root ID");
        PersistenceValueValidationV1.ValidateShortText(
            value.Name,
            "MIDI Channel Root name",
            allowEmpty: false);
        if (!Enum.IsDefined((MidiChannelRootRoutingMode)(int)value.RoutingMode)
            || !Enum.IsDefined((MidiChannelMode)(int)value.ChannelMode)
            || value.FixedZeroBasedPort > 15
            || value.FixedZeroBasedChannel > 15)
        {
            throw new InvalidDataException("MIDI Channel Root routing fields are invalid.");
        }
        HashSet<MidoraId> ids = [];
        foreach (long item in value.MidiTrackIds)
        {
            if (!ids.Add(ProtobufValueCodecV1.FromWire(item, "MIDI Channel Root Track ID")))
            {
                throw new InvalidDataException("MIDI Channel Root Track IDs contain a duplicate.");
            }
        }
    }
}

internal static class PureMidiTrackProtobufCodecV1
{
    public const string ObjectType = "pure-midi-track";

    public static byte[] Serialize(PureMidiTrack value, string contentPackPath)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPackPath);
        PureMidiTrackV1 wire = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            ObjectType = ObjectType,
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            Name = value.Name,
            MidiChannelRootId = ProtobufValueCodecV1.ToWire(value.MidiChannelRootId),
            ContentPackPath = contentPackPath
        };
        if (value.Color.HasValue)
        {
            wire.Color = ProtobufValueCodecV1.ToWire(value.Color.Value);
        }
        wire.Segments.Add(value.Segments.Select(ToWire));
        Validate(wire);
        return StrictProtobufWireV1.SerializeDeterministic(wire);
    }

    public static RestoredPureMidiTrackV1 Restore(MidoraProject project, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            StrictProtobufWireV1.Validate(bytes, PureMidiTrackV1.Descriptor);
            PureMidiTrackV1 wire = PureMidiTrackV1.Parser.ParseFrom(bytes);
            Validate(wire);
            PureMidiTrack result = new(
                project,
                ProtobufValueCodecV1.FromWire(wire.Id, "Pure MIDI Track ID"))
            {
                Name = wire.Name,
                MidiChannelRootId = ProtobufValueCodecV1.FromWire(
                    wire.MidiChannelRootId,
                    "Pure MIDI Track Root ID"),
                Color = wire.Color is null
                    ? null
                    : ProtobufValueCodecV1.FromWire(wire.Color, "Pure MIDI Track color")
            };
            result.Segments.AddRange(wire.Segments.Select(value => FromWire(project, value)));
            return new(result, wire.ContentPackPath);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidDataException("Pure MIDI Track protobuf is malformed.", exception);
        }
    }

    private static MidiSegmentV1 ToWire(MidiSegment value)
    {
        MidiSegmentV1 result = new()
        {
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            ProjectStartTick = value.ProjectStartTick,
            LengthTicks = value.LengthTicks,
            ContentOffsetTick = value.ContentOffsetTick
        };
        return result;
    }

    private static MidiSegment FromWire(MidoraProject project, MidiSegmentV1 value)
    {
        MidiSegment result = new(
            project,
            ProtobufValueCodecV1.FromWire(value.Id, "MIDI Segment ID"))
        {
            ProjectStartTick = value.ProjectStartTick,
            LengthTicks = value.LengthTicks,
            ContentOffsetTick = value.ContentOffsetTick
        };
        return result;
    }

    private static void Validate(PureMidiTrackV1 value)
    {
        ProtobufValueCodecV1.Require(value.HasSchemaVersion, "Pure MIDI Track schemaVersion");
        ProtobufValueCodecV1.Require(value.HasObjectType, "Pure MIDI Track objectType");
        ProtobufValueCodecV1.Require(value.HasName, "Pure MIDI Track name");
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion
            || value.ObjectType != ObjectType)
        {
            throw new ProtobufObjectHeaderExceptionV1(
                "Pure MIDI Track schemaVersion or objectType is inconsistent with its manifest identity.");
        }
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Pure MIDI Track ID");
        _ = ProtobufValueCodecV1.FromWire(value.MidiChannelRootId, "Pure MIDI Track Root ID");
        ProtobufValueCodecV1.Require(value.HasContentPackPath, "Pure MIDI Track contentPackPath");
        PersistenceValueValidationV1.ValidateShortText(value.Name, "Pure MIDI Track name");
        string expectedPackPath = MidoraPackagePathsV1.PureMidiContentPack(
            ProtobufValueCodecV1.FromWire(value.Id, "Pure MIDI Track ID"));
        if (!string.Equals(value.ContentPackPath, expectedPackPath, StringComparison.Ordinal))
            throw new InvalidDataException("Pure MIDI Track contentPackPath is not canonical for its stable ID.");
        if (value.Color is not null)
        {
            _ = ProtobufValueCodecV1.FromWire(value.Color, "Pure MIDI Track color");
        }
        foreach (MidiSegmentV1 segment in value.Segments)
        {
            Validate(segment);
        }
    }

    private static void Validate(MidiSegmentV1 value)
    {
        _ = ProtobufValueCodecV1.FromWire(value.Id, "MIDI Segment ID");
        ProtobufValueCodecV1.Require(value.HasProjectStartTick, "MIDI Segment projectStartTick");
        ProtobufValueCodecV1.Require(value.HasLengthTicks, "MIDI Segment lengthTicks");
        ProtobufValueCodecV1.Require(value.HasContentOffsetTick, "MIDI Segment contentOffsetTick");
    }
}

internal sealed record RestoredPureMidiTrackV1(PureMidiTrack Track, string ContentPackPath);
