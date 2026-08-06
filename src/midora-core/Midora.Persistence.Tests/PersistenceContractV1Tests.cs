using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;

namespace Midora.Persistence.Tests;

public sealed class PersistenceContractV1Tests
{
    [Fact]
    public void ContractVersionsAndTextLimitsAreFrozen()
    {
        Assert.Equal(1, PersistenceContractV1.FileFormatVersion);
        Assert.Equal(1, PersistenceContractV1.SchemaVersion);
        Assert.Equal("2024", PersistenceContractV1.ProtobufEdition);
        Assert.Equal("3.35.1", PersistenceContractV1.GoogleProtobufVersion);
        Assert.Equal("2.83.0", PersistenceContractV1.GrpcToolsVersion);
        Assert.Equal(256, PersistenceContractV1.ShortTextMaximumScalars);
        Assert.Equal(4_096, PersistenceContractV1.MetadataTextMaximumScalars);
        Assert.Equal(65_536, PersistenceContractV1.DescriptionMaximumScalars);
        Assert.Equal(1_048_576, PersistenceContractV1.MappingBodyMaximumScalars);
        Assert.Equal(4_096, PersistenceContractV1.RelativePathMaximumScalars);
    }

    [Fact]
    public void TimestampUsesFixedSevenDigitUtcForm()
    {
        DateTimeOffset value = new(2026, 8, 6, 12, 34, 56, TimeSpan.FromHours(8));
        value = value.AddTicks(1_234_567);

        string text = PersistenceContractV1.FormatUtcTimestamp(value);

        Assert.Equal("2026-08-06T04:34:56.1234567Z", text);
        Assert.True(PersistenceContractV1.TryParseUtcTimestamp(text, out DateTimeOffset parsed));
        Assert.Equal(value, parsed);
        Assert.False(PersistenceContractV1.TryParseUtcTimestamp("2026-08-06T04:34:56Z", out _));
        Assert.False(PersistenceContractV1.TryParseUtcTimestamp("2026-08-06T12:34:56.1234567+08:00", out _));
        PersistenceValueValidationV1.ValidateEditingDuration(long.MaxValue, "totalEditingTimeMilliseconds");
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateEditingDuration(-1, "totalEditingTimeMilliseconds"));
    }

    [Fact]
    public void TextAndPathValidationUsesUnicodeScalarsWithoutNormalization()
    {
        string decomposed = "e\u0301";
        PersistenceValueValidationV1.ValidateShortText(decomposed, "name", allowEmpty: false);
        Assert.Equal(decomposed, decomposed.Normalize(NormalizationForm.FormD));
        PersistenceValueValidationV1.ValidateShortText(string.Concat(Enumerable.Repeat("\U0001f3b5", 256)), "name");

        PersistenceValueValidationV1.ValidateRelativePath("soundfonts/音色.sf2", "path");
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateRelativePath("soundfonts/../escape.sf2", "path"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateRelativePath("C:/soundfonts/a.sf2", "path"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateRelativePath("soundfonts\\a.sf2", "path"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateRelativePath("soundfonts/", "path"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateShortText(string.Concat(Enumerable.Repeat("\U0001f3b5", 257)), "name"));
    }

    [Fact]
    public void DomainColorIsOpaqueRgb()
    {
        MidoraColor color = MidoraColor.DefaultInstrument;

        Assert.Equal((byte)0x6b, color.Red);
        Assert.Equal((byte)0x72, color.Green);
        Assert.Equal((byte)0x80, color.Blue);
        Assert.DoesNotContain(typeof(MidoraColor).GetProperties(), property => property.Name is "Alpha" or "Argb");
        PersistenceValueValidationV1.ValidateRgb(color.Red, color.Green, color.Blue, "color");
        Assert.Throws<InvalidDataException>(() => PersistenceValueValidationV1.ValidateRgb(256, 0, 0, "color"));
    }

    [Fact]
    public void ProtobufPrimitiveFieldNumbersAndGoldenBytesAreFrozen()
    {
        Assert.True(StableId.Descriptor.File.ToProto().HasEdition);
        Assert.Equal(1001, (int)StableId.Descriptor.File.ToProto().Edition);
        Assert.Equal(1, StableId.Descriptor.FindFieldByName("high")!.FieldNumber);
        Assert.Equal(2, StableId.Descriptor.FindFieldByName("low")!.FieldNumber);
        Assert.Equal(1, RgbColor.Descriptor.FindFieldByName("red")!.FieldNumber);
        Assert.Equal(2, RgbColor.Descriptor.FindFieldByName("green")!.FieldNumber);
        Assert.Equal(3, RgbColor.Descriptor.FindFieldByName("blue")!.FieldNumber);

        PersistenceValueValidationV1.ValidateStableId(
            0x0102030405060708, 0x1112131415161718, "id");
        PersistenceValueValidationV1.ValidateStableIdText(
            "01020304050607081112131415161718", "id");
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateStableId(0, 0, "id"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateStableIdText(new string('0', 32), "id"));

        StableId id = new() { High = 0x0102030405060708, Low = 0x1112131415161718 };
        byte[] bytes = StrictProtobufWireV1.SerializeDeterministic(id);

        Assert.Equal("090807060504030201111817161514131211", Convert.ToHexString(bytes).ToLowerInvariant());
        StrictProtobufWireV1.Validate(bytes, StableId.Descriptor);
    }

    [Fact]
    public void StrictProtobufRejectsUnknownFieldsWrongWireTypesAndInvalidRgb()
    {
        byte[] unknownField = Convert.FromHexString("0901000000000000001801");
        byte[] wrongWireType = Convert.FromHexString("0801");
        byte[] uint32Overflow = Convert.FromHexString("088080808010");
        byte[] nonFiniteDouble = Convert.FromHexString("09000000000000f87f");

        Assert.Throws<InvalidDataException>(() =>
            StrictProtobufWireV1.Validate(unknownField, StableId.Descriptor));
        Assert.Throws<InvalidDataException>(() =>
            StrictProtobufWireV1.Validate(wrongWireType, StableId.Descriptor));
        Assert.Throws<InvalidDataException>(() =>
            StrictProtobufWireV1.Validate(uint32Overflow, RgbColor.Descriptor));
        Assert.Throws<InvalidDataException>(() =>
            StrictProtobufWireV1.Validate(nonFiniteDouble, DoubleValue.Descriptor));

        RgbColor invalid = new() { Red = 256 };
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateRgb(invalid.Red, invalid.Green, invalid.Blue, "color"));
    }

    [Fact]
    public void ManifestJsonIsStrictAndDeterministic()
    {
        ManifestJsonV1 manifest = CreateManifest();

        byte[] first = ManifestCodecV1.Serialize(manifest);
        byte[] second = ManifestCodecV1.Serialize(manifest);

        Assert.Equal(first, second);
        Assert.Equal((byte)'\n', first[^1]);
        Assert.DoesNotContain((byte)'\r', first);
        Assert.False(first.Length >= 3
            && first[0] == 0xef
            && first[1] == 0xbb
            && first[2] == 0xbf);
        ManifestJsonV1 parsed = ManifestCodecV1.Parse(first);
        Assert.Equal("project.json", parsed.Files[0].Path);

        string duplicate = """
            {"magic":"midora-project","magic":"midora-project"}
            """;
        Assert.Throws<InvalidDataException>(() => ManifestCodecV1.Parse(Encoding.UTF8.GetBytes(duplicate)));

        string unknown = Encoding.UTF8.GetString(first).Replace(
            "\"files\": [",
            "\"unknown\": 1,\n  \"files\": [",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ManifestCodecV1.Parse(Encoding.UTF8.GetBytes(unknown)));

        string futureKind = Encoding.UTF8.GetString(first).Replace(
            "\"embedded-resource\"",
            "\"future-extension\"",
            StringComparison.Ordinal);
        ManifestJsonV1 withFutureKind = ManifestCodecV1.Parse(Encoding.UTF8.GetBytes(futureKind));
        Assert.Equal("future-extension", withFutureKind.Files[1].Kind);
        Assert.Throws<InvalidDataException>(() => ManifestCodecV1.Serialize(withFutureKind));
    }

    [Fact]
    public void CheckedInJsonSchemasUseDraft202012AndStrictObjects()
    {
        string schemaDirectory = Path.Combine(AppContext.BaseDirectory, "Schemas", "Json");
        string[] paths = Directory.GetFiles(schemaDirectory, "*.schema.json", SearchOption.TopDirectoryOnly);
        Assert.Equal(4, paths.Length);
        foreach (string path in paths)
        {
            using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.Equal(PersistenceContractV1.JsonSchemaDialect,
                schema.RootElement.GetProperty("$schema").GetString());
        }
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(schemaDirectory, "manifest-v1.schema.json")));
        Assert.False(manifest.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void CheckedInDescriptorHashMatchesFrozenProtoDescriptor()
    {
        FileDescriptorSet descriptorSet = new();
        descriptorSet.File.Add(StableId.Descriptor.File.ToProto());
        byte[] descriptorBytes = StrictProtobufWireV1.SerializeDeterministic(descriptorSet);
        string actual = Convert.ToHexString(SHA256.HashData(descriptorBytes)).ToLowerInvariant();
        string baselinePath = Path.Combine(
            AppContext.BaseDirectory, "Schemas", "Proto", "midora-common-v1.descriptor.sha256");

        Assert.Equal(File.ReadAllText(baselinePath).Trim(), actual);
    }

    private static ManifestJsonV1 CreateManifest() => new()
    {
        Magic = "midora-project",
        FileFormatVersion = 1,
        MinimumReadableVersion = 1,
        ManifestSchemaVersion = 1,
        CreatedWithSoftwareVersion = "0.1.0",
        LastSavedWithSoftwareVersion = "0.1.0",
        Files =
        [
            new ManifestFileEntryJsonV1
            {
                Path = "resources/soundfonts/00000000000000000000000000000001.sf2",
                Kind = "embedded-resource",
                Sha256 = new string('a', 64)
            },
            new ManifestFileEntryJsonV1
            {
                Path = "project.json",
                Kind = "core-json",
                SchemaVersion = 1,
                Sha256 = new string('0', 64)
            }
        ]
    };
}
