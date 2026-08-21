using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class WpfInteractionRegressionTests
{
    [Fact]
    public void ProjectEditsQueueBoundCollectionRefreshesOnTheDispatcher()
    {
        RunOnSta(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            int dispatcherThreadId = Environment.CurrentManagedThreadId;
            DesktopSessionController session = new();
            try
            {
                PumpUntil(session.CreateProjectAsync(new NewProjectCreationRequest
                {
                    ProjectName = "WPF refresh",
                    PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
                }));
                session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
                EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
                DrainDispatcher();

                ProjectTreeNode tracks = session.ProjectTree.Single(
                    item => item.Kind == ProjectTreeNodeKind.LogicalTracks);
                Assert.Empty(tracks.Children);
                List<int> collectionChangeThreads = [];
                session.ProjectTree.CollectionChanged += (_, _) =>
                    collectionChangeThreads.Add(Environment.CurrentManagedThreadId);

                session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));

                Assert.Single(session.Project!.Tracks);
                Assert.Empty(tracks.Children);
                DrainDispatcher();

                ProjectTreeNode refreshedTracks = session.ProjectTree.Single(
                    item => item.Kind == ProjectTreeNodeKind.LogicalTracks);
                Assert.Single(refreshedTracks.Children);
                Assert.NotEmpty(collectionChangeThreads);
                Assert.All(collectionChangeThreads, threadId =>
                    Assert.Equal(dispatcherThreadId, threadId));

                collectionChangeThreads.Clear();
                PumpUntil(Task.Run(() =>
                    session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Background Instrument"))));
                DrainDispatcher();
                ProjectTreeNode library = session.ProjectTree.Single(
                    item => item.Kind == ProjectTreeNodeKind.InstrumentLibrary);
                Assert.Contains(library.Children, item => item.Title == "Background Instrument");
                Assert.NotEmpty(collectionChangeThreads);
                Assert.All(collectionChangeThreads, threadId =>
                    Assert.Equal(dispatcherThreadId, threadId));

                Assert.IsType<InstrumentWorkspaceViewModel>(session.OpenInstrument(instrument.Id));
                Assert.Equal(
                    WorkspaceKind.ConductorTrack,
                    session.OpenWorkspace(session.ProjectTree.Single(
                        item => item.Kind == ProjectTreeNodeKind.Conductor)).Kind);
            }
            finally
            {
                PumpUntil(session.DisposeAsync().AsTask());
            }
        });
    }

    [Fact]
    public void CaptionButtonStyleIsInteractiveInsideWindowChrome()
    {
        RunOnSta(() =>
        {
            ResourceDictionary controls = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri(
                    "/Midora.Desktop.Presentation;component/Themes/Controls.xaml",
                    UriKind.Relative));
            Style caption = Assert.IsType<Style>(controls["Button.Caption"]);

            Setter setter = Assert.Single(caption.Setters.OfType<Setter>(), item =>
                item.Property == WindowChrome.IsHitTestVisibleInChromeProperty);
            Assert.Equal(true, setter.Value);
        });
    }

    [Fact]
    public void PianoRollVerticalViewportStaysInsideMidiPitchRange()
    {
        RunOnSta(() =>
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll,
                LaneHeight = 24
            };
            surface.Measure(new Size(500, 504));
            surface.Arrange(new Rect(0, 0, 500, 504));

            surface.FirstLane = int.MaxValue;
            Assert.Equal(20, surface.VisibleLaneCount);
            Assert.Equal(108, surface.MaximumFirstLane);
            Assert.Equal(108, surface.FirstLane);

            surface.FirstLane = -1;
            Assert.Equal(0, surface.FirstLane);

            TimelineSurface partialLaneSurface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll,
                LaneHeight = 18
            };
            partialLaneSurface.Measure(new Size(500, 500));
            partialLaneSurface.Arrange(new Rect(0, 0, 500, 500));
            partialLaneSurface.FirstLane = int.MaxValue;

            Assert.Equal(26, partialLaneSurface.VisibleLaneCount);
            Assert.Equal(102, partialLaneSurface.MaximumFirstLane);
            Assert.Equal(102, partialLaneSurface.FirstLane);

            const int width = 500;
            const int height = 1_112;
            TimelineSurface oversizedSurface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll,
                Width = width,
                Height = height
            };
            oversizedSurface.Measure(new Size(width, height));
            oversizedSurface.Arrange(new Rect(0, 0, width, height));
            oversizedSurface.LaneHeight = TimelineSurface.MinimumPianoLaneHeight;

            Assert.Equal(128, oversizedSurface.VisibleLaneCount);
            Assert.Equal(0, oversizedSurface.MaximumFirstLane);
            Assert.Equal(0, oversizedSurface.FirstLane);

            RenderTargetBitmap target = new(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(oversizedSurface);
            byte[] pixel = new byte[4];
            target.CopyPixels(new Int32Rect(100, height - 8, 1, 1), pixel, 4, 0);
            Assert.Equal([14, 11, 9, 255], pixel);
        });
    }

    [Fact]
    public void OptInImportedPagedMidiRendersThroughTheWpfTimelineSurface()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_UI_SAMPLE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;

        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            Path.GetFullPath(path),
            Path.GetFileNameWithoutExtension(path));
        try
        {
            MidiSegment segment = imported.Project.PureMidiTracks
                .SelectMany(track => track.Segments)
                .First(candidate => candidate.Notes.Count != 0);
            DirectMidiNoteValue first = segment.Notes.QueryValues(
                    segment.ContentOffsetTick,
                    segment.ContentEndTick)
                .First();
            TimelineWorkspaceViewModel workspace = new(
                WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
                "MIDI Segment",
                TimelineWorkspaceMode.Segment);
            workspace.Rebuild(imported.Project, revision: 1);

            RunOnSta(() =>
            {
                const int width = 800;
                const int height = 260;
                int noteLane = 127 - first.Key;
                long startTick = Math.Max(
                    segment.ContentOffsetTick,
                    first.StartTick - Math.Min(16, first.StartTick));
                long tickSpan = Math.Max(64, checked(first.LengthTicks + 32));
                TimelineSurface surface = new()
                {
                    SurfaceMode = TimelineSurfaceMode.PianoRoll,
                    StartTick = startTick,
                    TickSpan = tickSpan,
                    FirstLane = Math.Max(0, noteLane - 4),
                    LaneHeight = 18,
                    RangeStartTick = segment.ContentOffsetTick,
                    RangeEndTick = segment.ContentEndTick,
                    GridVisible = false
                };
                surface.Measure(new Size(width, height));
                surface.Arrange(new Rect(0, 0, width, height));
                TimelineRasterCacheSession.Clear();

                RenderTargetBitmap target = new(width, height, 96, 96, PixelFormats.Pbgra32);
                target.Render(surface);
                byte[] baseline = new byte[width * height * 4];
                target.CopyPixels(baseline, width * 4, 0);

                surface.Snapshot = workspace.Snapshot;
                bool changed = false;
                for (int attempt = 0; attempt < 200 && !changed; attempt++)
                {
                    DrainDispatcher();
                    Thread.Sleep(5);
                    target = new(width, height, 96, 96, PixelFormats.Pbgra32);
                    target.Render(surface);
                    byte[] actual = new byte[baseline.Length];
                    target.CopyPixels(actual, width * 4, 0);
                    changed = !actual.AsSpan().SequenceEqual(baseline);
                }

                Assert.True(changed, "The source-backed MIDI Note tiles never reached the WPF surface.");
            });
        }
        finally
        {
            imported.Project.Dispose();
        }
    }

    [Fact]
    public void ComboAndScrollBarThemesProvideDedicatedTemplatesAndChevronGeometry()
    {
        RunOnSta(() =>
        {
            ResourceDictionary icons = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/FluentSystemIcons.xaml", UriKind.Relative));
            ResourceDictionary palette = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/Palette.xaml", UriKind.Relative));
            System.Windows.Application application = new();
            application.Resources.MergedDictionaries.Add(palette);
            application.Resources.MergedDictionaries.Add(icons);
            ResourceDictionary controls = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/Controls.xaml", UriKind.Relative));
            application.Resources.MergedDictionaries.Add(controls);
            try
            {
                Style combo = Assert.IsType<Style>(controls[typeof(ComboBox)]);
                Style scrollBar = Assert.IsType<Style>(controls[typeof(ScrollBar)]);
                Style menuSeparator = Assert.IsType<Style>(
                    controls[MenuItem.SeparatorStyleKey]);

                Assert.Contains(combo.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate);
                Assert.Contains(scrollBar.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate);
                Assert.Equal(typeof(Separator), menuSeparator.TargetType);
                Assert.Contains(menuSeparator.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.TemplateProperty
                    && setter.Value is ControlTemplate);
                Assert.Contains(combo.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.VerticalContentAlignmentProperty
                    && Equals(setter.Value, VerticalAlignment.Center));
                Assert.Contains(combo.Setters.OfType<Setter>(), setter =>
                    setter.Property == ComboBoxWheelSelectionGuard.IsEnabledProperty
                    && Equals(setter.Value, true));

                ComboBox displayMemberCombo = new()
                {
                    DisplayMemberPath = nameof(MidiControlChangeInfo.DisplayName),
                    ItemsSource = new[] { new MidiControlChangeInfo(1, "Modulation Wheel (MSB)") },
                    SelectedIndex = 0,
                    Background = Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Surface.0"]),
                    BorderBrush = Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Border.Strong"]),
                    Foreground = Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Text.Primary"])
                };
                displayMemberCombo.Style = combo;
                displayMemberCombo.Measure(new Size(300, 32));
                displayMemberCombo.Arrange(new Rect(0, 0, 300, 32));
                displayMemberCombo.ApplyTemplate();
                Assert.True(ComboBoxWheelSelectionGuard.GetIsEnabled(displayMemberCombo));
                ContentPresenter contentSite = Assert.IsType<ContentPresenter>(
                    displayMemberCombo.Template.FindName("ContentSite", displayMemberCombo));
                Assert.NotNull(contentSite.ContentTemplateSelector);

                MouseWheelEventArgs closedWheel = new(Mouse.PrimaryDevice, 0, 120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = displayMemberCombo
                };
                displayMemberCombo.RaiseEvent(closedWheel);
                Assert.True(closedWheel.Handled);

                Assert.IsAssignableFrom<System.Windows.Media.Geometry>(icons["Fluent.ChevronDown20Regular"]);
                Assert.Equal(
                    System.Windows.Media.Color.FromRgb(66, 78, 88),
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Segment"]).Color);
                Assert.Equal(
                    System.Windows.Media.Color.FromRgb(2, 3, 4),
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Segment.PianoOutside"]).Color);
                Assert.Equal(
                    System.Windows.Media.Color.FromRgb(189, 199, 207),
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Segment.NotePreview"]).Color);
                Assert.Equal(
                    System.Windows.Media.Color.FromRgb(163, 178, 190),
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.Segment.PianoNote"]).Color);
                Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.PianoKey.White"]);
                Assert.IsType<System.Windows.Media.SolidColorBrush>(palette["Brush.PianoKey.Black"]);
            }
            finally
            {
                application.Shutdown();
            }
        });
    }

    private static void PumpUntil(Task task)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherFrame frame = new();
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void DrainDispatcher()
    {
        DispatcherFrame frame = new();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The WPF Dispatcher regression test did not complete.");
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

}
