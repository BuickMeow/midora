using System.IO.Compression;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;

namespace Midora.Persistence.Tests;

public sealed class ProjectObjectProtobufV1Tests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 6, 1, 2, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset SavedAt =
        new(2026, 8, 6, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public void ObjectCodecsRoundTripCompleteSourceGraphDeterministically()
    {
        MidoraProject source = CreateObjectProject();
        EventInstrument sourceInstrument = Assert.Single(source.EventInstruments);
        LogicalTrack sourceTrack = Assert.Single(source.Tracks);

        byte[] firstInstrument = EventInstrumentProtobufCodecV1.Serialize(sourceInstrument);
        byte[] secondInstrument = EventInstrumentProtobufCodecV1.Serialize(sourceInstrument);
        byte[] firstTrack = LogicalTrackProtobufCodecV1.Serialize(sourceTrack);
        byte[] secondTrack = LogicalTrackProtobufCodecV1.Serialize(sourceTrack);
        MidoraProject restoredProject = new(480, CreatedAt);
        EventInstrument restoredInstrument = EventInstrumentProtobufCodecV1.Restore(
            restoredProject,
            firstInstrument);
        LogicalTrack restoredTrack = LogicalTrackProtobufCodecV1.Restore(restoredProject, firstTrack);

        Assert.Equal(firstInstrument, secondInstrument);
        Assert.Equal(firstTrack, secondTrack);
        Assert.Equal(firstInstrument, EventInstrumentProtobufCodecV1.Serialize(restoredInstrument));
        Assert.Equal(firstTrack, LogicalTrackProtobufCodecV1.Serialize(restoredTrack));
        Assert.Equal(sourceInstrument.Id, restoredInstrument.Id);
        Assert.Equal(sourceInstrument.Description, restoredInstrument.Description);
        Assert.Equal(sourceInstrument.LogicalParameters[0].Id, restoredInstrument.LogicalParameters[0].Id);
        Assert.Equal(
            sourceInstrument.LogicalParameters[0].EnumItems[0].Id,
            restoredInstrument.LogicalParameters[0].EnumItems[0].Id);
        Assert.Equal(sourceInstrument.SubVoices[0].Id, restoredInstrument.SubVoices[0].Id);
        Assert.Equal(sourceInstrument.SubVoices[0].Events[0].Id, restoredInstrument.SubVoices[0].Events[0].Id);
        Assert.Equal(
            sourceInstrument.SubVoices[0].Events[0].NumberMappings.Id,
            restoredInstrument.SubVoices[0].Events[0].NumberMappings.Id);
        Assert.Equal(
            sourceInstrument.SubVoices[0].Events[0].NumberMappings[0].Id,
            restoredInstrument.SubVoices[0].Events[0].NumberMappings[0].Id);
        Assert.Equal(sourceInstrument.SubVoices[0].Curves[0].Id, restoredInstrument.SubVoices[0].Curves[0].Id);
        Assert.Equal(
            sourceInstrument.SubVoices[0].Curves[0].Points[0].Id,
            restoredInstrument.SubVoices[0].Curves[0].Points[0].Id);
        Assert.Equal(sourceInstrument.Envelopes[0].Id, restoredInstrument.Envelopes[0].Id);
        Assert.Equal(sourceInstrument.MappingFunctions[0].Id, restoredInstrument.MappingFunctions[0].Id);
        Assert.Equal(sourceInstrument.ParameterMappings[0].Id, restoredInstrument.ParameterMappings[0].Id);
        Assert.Equal(sourceInstrument.ParameterMappings[0].Steps.Id, restoredInstrument.ParameterMappings[0].Steps.Id);
        Assert.Equal(sourceTrack.Id, restoredTrack.Id);
        Assert.Equal(sourceTrack.Segments[0].Id, restoredTrack.Segments[0].Id);
        Assert.Equal(sourceTrack.Segments[0].Notes[0].Id, restoredTrack.Segments[0].Notes[0].Id);
        Assert.Equal(
            sourceTrack.Segments[0].ParameterLanes[0].Id,
            restoredTrack.Segments[0].ParameterLanes[0].Id);
        Assert.Equal(
            sourceTrack.Segments[0].ParameterLanes[0].Points[0].Id,
            restoredTrack.Segments[0].ParameterLanes[0].Points[0].Id);
    }

    [Fact]
    public async Task PackageRoundTripsCompleteObjectGraphWithStableBytesAndIds()
    {
        using TemporaryDirectory temporary = new();
        string firstPath = temporary.PathFor("objects-a.midora");
        string secondPath = temporary.PathFor("objects-b.midora");
        MidoraProject source = CreateObjectProject();
        UInt128 nextStableId = source.NextStableId;
        MidoraProjectPackageV1 packages = CreateService();

        await packages.SaveCopyAsync(source, firstPath);
        await packages.SaveCopyAsync(source, secondPath);
        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(firstPath);

        Assert.Equal(await File.ReadAllBytesAsync(firstPath), await File.ReadAllBytesAsync(secondPath));
        Assert.False(opened.IsModified);
        Assert.Empty(opened.Diagnostics);
        Assert.Empty(opened.Project.DamagedEventInstruments);
        Assert.Empty(opened.Project.DamagedLogicalTracks);
        Assert.Equal(nextStableId, opened.Project.NextStableId);
        Assert.Single(opened.Project.EventInstrumentFolders);
        EventInstrument instrument = Assert.Single(opened.Project.EventInstruments);
        LogicalTrack track = Assert.Single(opened.Project.Tracks);
        Assert.Equal(opened.Project.EventInstrumentFolders[0].Id, instrument.LibraryFolderId);
        Assert.Equal(instrument.Id, track.EventInstrumentId);
        Assert.Contains(track.Id, opened.Project.AudioRender.ExplicitLogicalTrackIds);
        Assert.Equal(
            EventInstrumentProtobufCodecV1.Serialize(source.EventInstruments[0]),
            EventInstrumentProtobufCodecV1.Serialize(instrument));
        Assert.Equal(
            LogicalTrackProtobufCodecV1.Serialize(source.Tracks[0]),
            LogicalTrackProtobufCodecV1.Serialize(track));

        using ZipArchive archive = ZipFile.OpenRead(firstPath);
        Assert.NotNull(archive.GetEntry($"event-instruments/ei_{instrument.Id}.pb"));
        Assert.NotNull(archive.GetEntry($"logical-tracks/lt_{track.Id}.pb"));
        Assert.Equal(
            new[]
            {
                "manifest.json",
                "project.json",
                "metadata.json",
                "conductor-track.json",
                "settings/project-settings.json",
                "settings/export-settings.json",
                "settings/playback-settings.json",
                "settings/audio-render-settings.json",
                "settings/soundfont-settings.json",
                "settings/global-reset-defaults.json",
                "settings/global-event-scope-defaults.json",
                $"event-instruments/ei_{instrument.Id}.pb",
                $"logical-tracks/lt_{track.Id}.pb"
            },
            archive.Entries.Select(entry => entry.FullName));
    }

    [Fact]
    public async Task MissingAndHashMismatchedObjectsBecomePlaceholdersAndDisableSave()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("damaged.midora");
        string savePath = temporary.PathFor("refused.midora");
        MidoraProject source = CreateObjectProject();
        string instrumentPath = $"event-instruments/ei_{source.EventInstruments[0].Id}.pb";
        string trackPath = $"logical-tracks/lt_{source.Tracks[0].Id}.pb";
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(source, packagePath);
        TamperEntryWithoutUpdatingManifest(packagePath, instrumentPath);
        DeleteEntry(packagePath, trackPath);

        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(packagePath);

        Assert.False(opened.IsModified);
        Assert.Empty(opened.Project.EventInstruments);
        Assert.Empty(opened.Project.Tracks);
        DamagedProjectObject damagedInstrument = Assert.Single(opened.Project.DamagedEventInstruments);
        DamagedProjectObject damagedTrack = Assert.Single(opened.Project.DamagedLogicalTracks);
        Assert.Equal(source.EventInstruments[0].Id, damagedInstrument.Id);
        Assert.Equal(source.Tracks[0].Id, damagedTrack.Id);
        Assert.Equal(0, damagedInstrument.OriginalIndex);
        Assert.Equal(0, damagedTrack.OriginalIndex);
        Assert.Equal(2, opened.Diagnostics.Count(item => item.Code == "MIDORA-PERSIST-DAMAGED-OBJECT"));

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.SaveCopyAsync(opened.Project, savePath));
        Assert.Equal(MidoraPackageStageV1.Serialization, failure.Stage);
        Assert.False(File.Exists(savePath));
    }

    [Fact]
    public async Task InternalIdMismatchAndUnknownTagBecomeDamagedPlaceholders()
    {
        using TemporaryDirectory temporary = new();
        string idPath = temporary.PathFor("id-mismatch.midora");
        string tagPath = temporary.PathFor("unknown-tag.midora");
        MidoraProject source = CreateObjectProject();
        string objectPath = $"event-instruments/ei_{source.EventInstruments[0].Id}.pb";
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(source, idPath);
        await packages.SaveCopyAsync(source, tagPath);

        byte[] original = ReadEntry(idPath, objectPath);
        EventInstrumentV1 wrongId = EventInstrumentV1.Parser.ParseFrom(original);
        wrongId.Id = new StableId { Low = 999 };
        ReplaceEntryAndUpdateManifest(
            idPath,
            objectPath,
            StrictProtobufWireV1.SerializeDeterministic(wrongId));
        byte[] unknownTag = [.. original, 0xb0, 0x01, 0x01];
        ReplaceEntryAndUpdateManifest(tagPath, objectPath, unknownTag);

        MidoraProjectOpenResultV1 idOpened = await packages.OpenAsync(idPath);
        MidoraProjectOpenResultV1 tagOpened = await packages.OpenAsync(tagPath);

        Assert.Single(idOpened.Project.DamagedEventInstruments);
        Assert.Single(tagOpened.Project.DamagedEventInstruments);
        Assert.Empty(idOpened.Project.EventInstruments);
        Assert.Empty(tagOpened.Project.EventInstruments);
    }

    [Fact]
    public async Task ObjectTypeMismatchFailsWholeProject()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("type-mismatch.midora");
        MidoraProject source = CreateObjectProject();
        string objectPath = $"event-instruments/ei_{source.EventInstruments[0].Id}.pb";
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(source, packagePath);
        EventInstrumentV1 wrongType = EventInstrumentV1.Parser.ParseFrom(ReadEntry(packagePath, objectPath));
        wrongType.ObjectType = LogicalTrackProtobufCodecV1.ObjectType;
        ReplaceEntryAndUpdateManifest(
            packagePath,
            objectPath,
            StrictProtobufWireV1.SerializeDeterministic(wrongType));

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.OpenAsync(packagePath));

        Assert.Equal(MidoraPackageStageV1.Structure, failure.Stage);
        Assert.Equal(objectPath, failure.PackagePath);
    }

    [Fact]
    public async Task ManifestIndexedOrphanObjectIsReportedAndRemovedOnSaveCopy()
    {
        using TemporaryDirectory temporary = new();
        string sourcePath = temporary.PathFor("orphan.midora");
        string cleanPath = temporary.PathFor("clean.midora");
        const string orphanPath =
            "event-instruments/ei_0000000000000000000000000000ffff.pb";
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(new MidoraProject(480, CreatedAt), sourcePath);
        AddManifestEntry(sourcePath, orphanPath, "event-instrument-pb", [1, 2, 3]);

        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(sourcePath);

        MidoraPackageDiagnosticV1 diagnostic = Assert.Single(
            opened.Diagnostics,
            item => item.Code == "MIDORA-PERSIST-INFO-ORPHAN-OBJECT");
        Assert.Equal(orphanPath, diagnostic.PackagePath);
        Assert.False(opened.IsModified);
        await packages.SaveCopyAsync(opened.Project, cleanPath);
        using ZipArchive clean = ZipFile.OpenRead(cleanPath);
        Assert.Null(clean.GetEntry(orphanPath));
    }

    [Fact]
    public async Task DamagedPlaceholderDeleteAndUndoPreserveBindingsAndAllowCleanSave()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("recover-object.midora");
        string cleanPath = temporary.PathFor("recovered.midora");
        MidoraProject source = CreateObjectProject();
        string instrumentPath = $"event-instruments/ei_{source.EventInstruments[0].Id}.pb";
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(source, packagePath);
        TamperEntryWithoutUpdatingManifest(packagePath, instrumentPath);
        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(packagePath);
        DamagedProjectObject placeholder = Assert.Single(opened.Project.DamagedEventInstruments);
        LogicalTrack track = Assert.Single(opened.Project.Tracks);
        Assert.Equal(placeholder.Id, track.EventInstrumentId);

        DamagedEventInstrumentDeletion deletion = DamagedProjectObjectEditing.DeleteEventInstrument(
            opened.Project,
            placeholder.Id);
        Assert.Null(track.EventInstrumentId);
        Assert.Equal(placeholder.NameSnapshot, track.LastBoundEventInstrumentName);
        DamagedProjectObjectEditing.UndoDeleteEventInstrument(opened.Project, deletion);
        Assert.Equal(placeholder.Id, track.EventInstrumentId);
        Assert.Single(opened.Project.DamagedEventInstruments);

        _ = DamagedProjectObjectEditing.DeleteEventInstrument(opened.Project, placeholder.Id);
        await packages.SaveCopyAsync(opened.Project, cleanPath);
        MidoraProjectOpenResultV1 clean = await packages.OpenAsync(cleanPath);
        Assert.Empty(clean.Diagnostics);
        Assert.Empty(clean.Project.DamagedEventInstruments);
        Assert.Empty(clean.Project.EventInstruments);
        Assert.Null(Assert.Single(clean.Project.Tracks).EventInstrumentId);
    }

    [Fact]
    public void PublishedObjectDescriptorsAndRepresentativeBytesAreFrozen()
    {
        Assert.Equal(1, EventInstrumentV1.Descriptor.FindFieldByName("schema_version")!.FieldNumber);
        Assert.Equal(3, EventInstrumentV1.Descriptor.FindFieldByName("id")!.FieldNumber);
        Assert.Equal(18, EventInstrumentV1.Descriptor.FindFieldByName("sub_voices")!.FieldNumber);
        Assert.Equal(1, LogicalTrackV1.Descriptor.FindFieldByName("schema_version")!.FieldNumber);
        Assert.Equal(3, LogicalTrackV1.Descriptor.FindFieldByName("id")!.FieldNumber);
        Assert.Equal(8, LogicalTrackV1.Descriptor.FindFieldByName("segments")!.FieldNumber);

        AssertDescriptorHash(
            EventInstrumentV1.Descriptor.File,
            "midora-event-instrument-v1.descriptor.sha256");
        AssertDescriptorHash(
            LogicalTrackV1.Descriptor.File,
            "midora-logical-track-v1.descriptor.sha256");

        MidoraProject minimal = new(480, CreatedAt);
        EventInstrument instrument = EventInstrumentLibrary.Create(minimal, "Minimal");
        LogicalTrack track = new(minimal) { Name = string.Empty, EventInstrumentId = instrument.Id };
        minimal.Tracks.Add(track);
        byte[] instrumentBytes = EventInstrumentProtobufCodecV1.Serialize(instrument);
        byte[] trackBytes = LogicalTrackProtobufCodecV1.Serialize(track);

        string instrumentBase64 = Convert.ToBase64String(instrumentBytes);
        string trackBase64 = Convert.ToBase64String(trackBytes);
        const string expectedInstrumentBase64 =
            "CAESEGV2ZW50LWluc3RydW1lbnQaCREDAAAAAAAAACIHTWluaW1hbCoHCGsQchiAATg8QOADSABQAFgAYABoAIIBAJIBDQoJEQQAAAAAAAAAIgA=";
        const string expectedTrackBase64 =
            "CAESDWxvZ2ljYWwtdHJhY2saCREFAAAAAAAAACIAKgkRAwAAAAAAAAA=";
        Assert.Equal(expectedInstrumentBase64, instrumentBase64);
        Assert.Equal(expectedTrackBase64, trackBase64);
    }

    private static void AssertDescriptorHash(FileDescriptor descriptor, string baselineName)
    {
        FileDescriptorSet descriptorSet = new();
        descriptorSet.File.Add(descriptor.ToProto());
        byte[] bytes = StrictProtobufWireV1.SerializeDeterministic(descriptorSet);
        string actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string baselinePath = Path.Combine(AppContext.BaseDirectory, "Schemas", "Proto", baselineName);
        Assert.Equal(File.ReadAllText(baselinePath).Trim(), actual);
    }

    private static MidoraProject CreateObjectProject()
    {
        MidoraProject project = new(480, CreatedAt);
        EventInstrumentLibraryFolder folder = EventInstrumentLibrary.CreateFolder(project, "Orchestra");
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Layered");
        instrument.LibraryFolderId = folder.Id;
        instrument.Description = "Complete object graph\nfor persistence.";
        instrument.Color = new MidoraColor(1, 2, 3);
        instrument.RootNote = 62;
        instrument.TemplateLengthTicks = 960;
        instrument.RequiresChannelIsolation = true;
        instrument.OverlapPolicy = OverlapPolicy.CutPrevious;
        instrument.OverlapScope = OverlapScope.AnyPitch;
        instrument.ShortLifecycle = ShortNoteLifecycle.Tail;
        instrument.LongLifecycle = LongNoteLifecycle.EndAtTemplate;
        instrument.LoopStartTick = 120;
        instrument.LoopEndTick = 840;
        instrument.InitialState.BankMsb = 1;
        instrument.InitialState.Controllers.Add(11, 100);

        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Expression",
            Type = LogicalParameterType.Enum,
            Minimum = 0,
            Maximum = 127,
            DisplayMinimum = -1,
            DisplayMaximum = 1,
            DefaultValue = 64,
            UsesExplicitEnumValues = true
        };
        parameter.EnumItems.Add(new LogicalParameterEnumItem(project) { Name = "Normal", Value = 64 });
        instrument.LogicalParameters.Add(parameter);

        CSharpMappingFunction function = new(project)
        {
            Name = "Scale",
            Body = "return value * context.TriggerVelocity;"
        };
        function.DeclaredContextFields.Add("TriggerVelocity");
        function.DeclaredContextFields.Add("ProjectTick");
        instrument.MappingFunctions.Add(function);

        InstrumentEnvelope envelope = new(project)
        {
            Name = string.Empty,
            DelayTicks = 1,
            AttackTicks = 2,
            HoldTicks = 3,
            DecayTicks = 4,
            StartValue = 0.1,
            PeakValue = 1,
            SustainValue = 0.7,
            ReleaseTicks = 5,
            EndValue = 0.2
        };
        instrument.Envelopes.Add(envelope);

        SubVoice voice = Assert.Single(instrument.SubVoices);
        voice.Name = "Main";
        voice.RootNoteOverride = 61;
        voice.InitialState.Program = 40;
        voice.InitialState.RegisteredParameters.Add(1, 2);
        TemplateEvent templateEvent = TemplateEvent.Bank(project, 10, 3, null);
        templateEvent.FollowPitchDelta = false;
        templateEvent.NumberMappings.IsEnabled = false;
        templateEvent.NumberMappings.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.CustomCSharp,
            LogicalParameterId = parameter.Id,
            EnvelopeId = envelope.Id,
            MappingFunctionId = function.Id,
            Constant = 2,
            SourceMinimum = -1,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127,
            InputOverflow = MappingInputOverflow.Extrapolate,
            DivideByZero = DivideByZeroPolicy.TargetDefault
        });
        templateEvent.NumberTargetSettings.Rounding = MappingRounding.Ceiling;
        templateEvent.NumberTargetSettings.Overflow = MappingOverflow.Clamp;
        voice.Events.Add(templateEvent);
        ValueCurve curve = new(project)
        {
            Target = MidiValueTarget.ControlChange(11)
        };
        curve.TargetSettings.Rounding = MappingRounding.Floor;
        curve.Points.Add(new CurvePoint(project, 0, 1.5, CurveInterpolation.Step));
        curve.Points.Add(new CurvePoint(project, 20, 2.5, CurveInterpolation.Linear));
        voice.Curves.Add(curve);

        LogicalParameterMapping mapping = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = voice.Id,
            Target = MidiValueTarget.PitchBendRangeCents
        };
        mapping.Steps.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Multiply,
            Constant = 0.5
        });
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
        instrument.ParameterMappings.Add(mapping);

        LogicalTrack track = new(project)
        {
            Name = string.Empty,
            EventInstrumentId = instrument.Id,
            LastBoundEventInstrumentName = "Layered",
            ColorOverride = new MidoraColor(9, 8, 7)
        };
        Segment segment = new(project)
        {
            ProjectStartTick = 240,
            LengthTicks = 480,
            ContentOffsetTick = 20
        };
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 20,
            LengthTicks = 120,
            Note = 65,
            Velocity = 90
        });
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        lane.Points.Add(new CurvePoint(project, 20, 64, CurveInterpolation.Step));
        lane.Points.Add(new CurvePoint(project, 100, 100, CurveInterpolation.Linear));
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        project.AudioRender.TrackSelectionMode = ProjectTrackSelectionMode.ExplicitLogicalTrackIds;
        project.AudioRender.ExplicitLogicalTrackIds.Add(track.Id);
        return project;
    }

    private static MidoraProjectPackageV1 CreateService() =>
        new("0.1.0-object-test", new FixedTimeProvider(SavedAt));

    private static byte[] ReadEntry(string packagePath, string entryName)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        using Stream input = archive.GetEntry(entryName)!.Open();
        using MemoryStream output = new();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void TamperEntryWithoutUpdatingManifest(string packagePath, string entryName)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)!.Delete();
        using Stream output = archive.CreateEntry(entryName).Open();
        output.WriteByte(0);
    }

    private static void DeleteEntry(string packagePath, string entryName)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)!.Delete();
    }

    private static void ReplaceEntryAndUpdateManifest(
        string packagePath,
        string entryName,
        byte[] replacementBytes)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        ManifestJsonV1 manifest;
        using (Stream manifestInput = archive.GetEntry("manifest.json")!.Open())
        using (MemoryStream buffer = new())
        {
            manifestInput.CopyTo(buffer);
            manifest = ManifestCodecV1.Parse(buffer.ToArray());
        }
        ManifestFileEntryJsonV1[] files = manifest.Files.Select(item => new ManifestFileEntryJsonV1
        {
            Path = item.Path,
            Kind = item.Kind,
            SchemaVersion = item.SchemaVersion,
            Sha256 = item.Path == entryName
                ? Convert.ToHexStringLower(SHA256.HashData(replacementBytes))
                : item.Sha256
        }).ToArray();
        ManifestJsonV1 updated = new()
        {
            Magic = manifest.Magic,
            FileFormatVersion = manifest.FileFormatVersion,
            MinimumReadableVersion = manifest.MinimumReadableVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            CreatedWithSoftwareVersion = manifest.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = manifest.LastSavedWithSoftwareVersion,
            Files = files
        };
        archive.GetEntry(entryName)!.Delete();
        using (Stream output = archive.CreateEntry(entryName).Open()) output.Write(replacementBytes);
        archive.GetEntry("manifest.json")!.Delete();
        using (Stream output = archive.CreateEntry("manifest.json").Open())
        {
            output.Write(ManifestCodecV1.Serialize(updated));
        }
    }

    private static void AddManifestEntry(
        string packagePath,
        string entryName,
        string kind,
        byte[] bytes)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        ManifestJsonV1 manifest;
        using (Stream manifestInput = archive.GetEntry("manifest.json")!.Open())
        using (MemoryStream buffer = new())
        {
            manifestInput.CopyTo(buffer);
            manifest = ManifestCodecV1.Parse(buffer.ToArray());
        }
        ManifestJsonV1 updated = new()
        {
            Magic = manifest.Magic,
            FileFormatVersion = manifest.FileFormatVersion,
            MinimumReadableVersion = manifest.MinimumReadableVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            CreatedWithSoftwareVersion = manifest.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = manifest.LastSavedWithSoftwareVersion,
            Files =
            [
                .. manifest.Files,
                new ManifestFileEntryJsonV1
                {
                    Path = entryName,
                    Kind = kind,
                    SchemaVersion = PersistenceContractV1.SchemaVersion,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
                }
            ]
        };
        using (Stream output = archive.CreateEntry(entryName).Open()) output.Write(bytes);
        archive.GetEntry("manifest.json")!.Delete();
        using (Stream output = archive.CreateEntry("manifest.json").Open())
        {
            output.Write(ManifestCodecV1.Serialize(updated));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-object-package-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string PathFor(string fileName) => System.IO.Path.Combine(Path, fileName);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
