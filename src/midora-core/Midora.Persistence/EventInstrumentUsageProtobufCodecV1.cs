using Google.Protobuf;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;

namespace Midora.Persistence;

internal static class EventInstrumentUsageProtobufCodecV1
{
    public const string ObjectType = "event-instrument-usage";

    public static byte[] Serialize(EventInstrumentUsage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EventInstrumentUsageV1 wire = new()
        {
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            ObjectType = ObjectType,
            Id = ProtobufValueCodecV1.ToWire(value.Id),
            EventInstrumentId = ProtobufValueCodecV1.ToWire(value.EventInstrumentId)
        };
        Validate(wire);
        return StrictProtobufWireV1.SerializeDeterministic(wire);
    }

    public static EventInstrumentUsage Restore(
        MidoraProject project,
        ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            StrictProtobufWireV1.Validate(bytes, EventInstrumentUsageV1.Descriptor);
            EventInstrumentUsageV1 wire = EventInstrumentUsageV1.Parser.ParseFrom(bytes);
            Validate(wire);
            return new EventInstrumentUsage(
                project,
                ProtobufValueCodecV1.FromWire(wire.Id, "Event Instrument Usage ID"))
            {
                EventInstrumentId = ProtobufValueCodecV1.FromWire(
                    wire.EventInstrumentId,
                    "Event Instrument Usage Definition ID")
            };
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidDataException(
                "Event Instrument Usage protobuf is malformed.",
                exception);
        }
    }

    private static void Validate(EventInstrumentUsageV1 value)
    {
        ProtobufValueCodecV1.Require(
            value.HasSchemaVersion,
            "Event Instrument Usage schemaVersion");
        ProtobufValueCodecV1.Require(
            value.HasObjectType,
            "Event Instrument Usage objectType");
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion
            || value.ObjectType != ObjectType)
        {
            throw new ProtobufObjectHeaderExceptionV1(
                "Event Instrument Usage schemaVersion or objectType is inconsistent with its manifest identity.");
        }
        _ = ProtobufValueCodecV1.FromWire(value.Id, "Event Instrument Usage ID");
        _ = ProtobufValueCodecV1.FromWire(
            value.EventInstrumentId,
            "Event Instrument Usage Definition ID");
    }
}
