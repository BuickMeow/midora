using Google.Protobuf;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;
using Midora.Persistence.Wire.Proto.V2;

namespace Midora.Persistence;

internal static class EventInstrumentProtobufCodecV2
{
    public const string ObjectType = EventInstrumentProtobufCodecV1.ObjectType;

    public static byte[] Serialize(EventInstrument value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] definitionBytes = EventInstrumentProtobufCodecV1.Serialize(value);
        EventInstrumentV2 wire = new()
        {
            SchemaVersion = PersistenceContractV2.EventInstrumentSchemaVersion,
            ObjectType = ObjectType,
            Definition = EventInstrumentV1.Parser.ParseFrom(definitionBytes),
            PreRollTicks = value.PreRollTicks
        };
        Validate(wire);
        return StrictProtobufWireV1.SerializeDeterministic(wire);
    }

    public static EventInstrument Restore(MidoraProject project, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            ValidateUniqueWrapperFields(bytes);
            StrictProtobufWireV1.Validate(bytes, EventInstrumentV2.Descriptor);
            EventInstrumentV2 wire = EventInstrumentV2.Parser.ParseFrom(bytes);
            Validate(wire);
            byte[] definitionBytes = StrictProtobufWireV1.SerializeDeterministic(wire.Definition);
            EventInstrument result = EventInstrumentProtobufCodecV1.Restore(project, definitionBytes);
            result.PreRollTicks = wire.PreRollTicks;
            return result;
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidDataException("Event Instrument v2 protobuf is malformed.", exception);
        }
    }

    private static void ValidateUniqueWrapperFields(ReadOnlySpan<byte> bytes)
    {
        CodedInputStream input = new(bytes.ToArray());
        uint seen = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            int fieldNumber = WireFormat.GetTagFieldNumber(tag);
            if (fieldNumber is >= 1 and <= 4)
            {
                uint mask = 1u << fieldNumber;
                if ((seen & mask) != 0)
                {
                    throw new InvalidDataException(
                        $"Event Instrument v2 protobuf field {fieldNumber} is duplicated.");
                }
                seen |= mask;
            }
            input.SkipLastField();
        }
    }

    private static void Validate(EventInstrumentV2 value)
    {
        ProtobufValueCodecV1.Require(value.HasSchemaVersion, "Event Instrument v2 schemaVersion");
        ProtobufValueCodecV1.Require(value.HasObjectType, "Event Instrument v2 objectType");
        ProtobufValueCodecV1.Require(value.HasPreRollTicks, "Event Instrument v2 preRollTicks");
        if (value.SchemaVersion != PersistenceContractV2.EventInstrumentSchemaVersion
            || value.ObjectType != ObjectType)
        {
            throw new ProtobufObjectHeaderExceptionV1(
                "Event Instrument v2 schemaVersion or objectType is inconsistent with its manifest identity.");
        }
        if (value.Definition is null)
        {
            throw new InvalidDataException("Event Instrument v2 definition is required.");
        }
        if (value.PreRollTicks < 0)
        {
            throw new InvalidDataException("Event Instrument preRollTicks must be non-negative.");
        }
        ProtobufValueCodecV1.Require(
            value.Definition.HasTemplateLengthTicks,
            "Event Instrument v2 definition templateLengthTicks");
        if (value.PreRollTicks > value.Definition.TemplateLengthTicks)
        {
            throw new InvalidDataException(
                "Event Instrument preRollTicks cannot exceed templateLengthTicks.");
        }

        // The frozen v1 codec remains the single validator for the reused definition payload.
        // Serialize has already passed through it, and Restore invokes it immediately after this
        // v2 header/range validation.
    }
}
