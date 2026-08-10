using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shell;
using System.Windows.Threading;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
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

                ProjectTreeNode tracks = session.ProjectTree.Single(
                    item => item.Kind == ProjectTreeNodeKind.LogicalTracks);
                Assert.Empty(tracks.Children);
                List<int> collectionChangeThreads = [];
                session.ProjectTree.CollectionChanged += (_, _) =>
                    collectionChangeThreads.Add(Environment.CurrentManagedThreadId);

                session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track"));

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
                    session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"))));
                DrainDispatcher();
                ProjectTreeNode library = session.ProjectTree.Single(
                    item => item.Kind == ProjectTreeNodeKind.InstrumentLibrary);
                Assert.Contains(library.Children, item => item.Title == "Instrument");
                Assert.NotEmpty(collectionChangeThreads);
                Assert.All(collectionChangeThreads, threadId =>
                    Assert.Equal(dispatcherThreadId, threadId));

                session.Execute(ProjectDomainEditCommands.CreateEventInstrumentFolder("Folder"));
                DrainDispatcher();
                Assert.Contains(
                    session.ProjectTree.Single(item => item.Kind == ProjectTreeNodeKind.InstrumentLibrary).Children,
                    item => item.Title == "Folder");

                EventInstrument instrument = Assert.Single(session.Project.EventInstruments);
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
        });
    }

    [Fact]
    public void ComboAndScrollBarThemesProvideDedicatedTemplatesAndChevronGeometry()
    {
        RunOnSta(() =>
        {
            ResourceDictionary controls = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/Controls.xaml", UriKind.Relative));
            ResourceDictionary icons = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/FluentSystemIcons.xaml", UriKind.Relative));
            ResourceDictionary palette = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Midora.Desktop.Presentation;component/Themes/Palette.xaml", UriKind.Relative));
            Style combo = Assert.IsType<Style>(controls[typeof(ComboBox)]);
            Style scrollBar = Assert.IsType<Style>(controls[typeof(ScrollBar)]);

            Assert.Contains(combo.Setters.OfType<Setter>(), setter =>
                setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate);
            Assert.Contains(scrollBar.Setters.OfType<Setter>(), setter =>
                setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate);
            Assert.Contains(combo.Setters.OfType<Setter>(), setter =>
                setter.Property == Control.VerticalContentAlignmentProperty
                && Equals(setter.Value, VerticalAlignment.Center));
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
