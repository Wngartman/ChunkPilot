using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ChunkPilot.App;
using ChunkPilot.App.CreateServerLive;
using ChunkPilot.Core;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>
/// The behaviour the UX-hardening pass introduced, and the defects it removed.
/// </summary>
/// <remarks>
/// Contracts rather than sentences: which folder a plan carries, whether Enter can reach a disabled
/// command, whether the scroll bar's target is larger than its pill. Copy is asserted only where a
/// label is the contract — a review row's label is what makes the screen scannable, so those are
/// pinned; prose is not.
/// </remarks>
public sealed class CreateServerV2UxHardeningTests
{
    private static readonly string AppDirectory = DesignSystemFiles.AppProjectDirectory;

    // ------------------------------------------------------------------ destination

    [Fact]
    public async Task The_default_location_is_the_managed_root_and_needs_no_choice()
    {
        var gateway = new LocationGateway();
        var model = await SetupAsync(gateway);

        await SetNameAsync(model, "Sunday survival");

        Assert.False(model.IsCustomLocation);
        Assert.Equal("", gateway.LastInstanceRoot);
        Assert.Contains(LocationGateway.ManagedRoot, model.DestinationSummary, StringComparison.Ordinal);
        Assert.Equal("Default", model.LocationModeText);
    }

    [Fact]
    public async Task Choosing_a_location_moves_the_destination_and_keeps_the_generated_folder_name()
    {
        var gateway = new LocationGateway();
        var chooser = new FakeChooser { Result = @"D:\Games" };
        var model = await SetupAsync(gateway, chooser);
        await SetNameAsync(model, "Sunday survival");

        await model.ChooseLocationCommand.ExecuteAsync(null);

        Assert.True(model.IsCustomLocation);
        Assert.Equal(@"D:\Games", gateway.LastInstanceRoot);
        Assert.Equal(Path.Combine(@"D:\Games", "Sunday-survival"), model.Destination!.CanonicalDestination);
        Assert.Equal("Custom", model.LocationModeText);
    }

    [Fact]
    public async Task Cancelling_the_chooser_changes_nothing()
    {
        var gateway = new LocationGateway();
        var model = await SetupAsync(gateway, new FakeChooser { Result = null });
        await SetNameAsync(model, "Sunday survival");
        var before = model.Destination!.CanonicalDestination;

        await model.ChooseLocationCommand.ExecuteAsync(null);

        Assert.False(model.IsCustomLocation);
        Assert.Equal(before, model.Destination!.CanonicalDestination);
    }

    [Fact]
    public async Task Renaming_after_choosing_a_location_keeps_that_location()
    {
        var gateway = new LocationGateway();
        var model = await SetupAsync(gateway, new FakeChooser { Result = @"D:\Games" });
        await SetNameAsync(model, "Sunday survival");
        await model.ChooseLocationCommand.ExecuteAsync(null);

        await SetNameAsync(model, "Weeknight world");

        // The explicit choice is the parent folder; only the generated child follows the name.
        Assert.True(model.IsCustomLocation);
        Assert.Equal(@"D:\Games", gateway.LastInstanceRoot);
        Assert.Equal(Path.Combine(@"D:\Games", "Weeknight-world"), model.Destination!.CanonicalDestination);
    }

    [Fact]
    public async Task Restoring_the_default_returns_to_the_managed_root()
    {
        var gateway = new LocationGateway();
        var model = await SetupAsync(gateway, new FakeChooser { Result = @"D:\Games" });
        await SetNameAsync(model, "Sunday survival");
        await model.ChooseLocationCommand.ExecuteAsync(null);

        await model.UseDefaultLocationCommand.ExecuteAsync(null);

        Assert.False(model.IsCustomLocation);
        Assert.Equal("", gateway.LastInstanceRoot);
        Assert.Contains(LocationGateway.ManagedRoot, model.Destination!.CanonicalDestination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_approved_plan_carries_the_location_the_review_screen_showed()
    {
        var gateway = new LocationGateway();
        var model = await ReviewAsync(gateway, new FakeChooser { Result = @"D:\Games" }, chooseLocation: true);
        model.EulaAccepted = true;

        var reviewed = model.Review.Sections
            .SelectMany(section => section.Rows)
            .First(row => row.Label == "Destination").Value;
        await model.CreateCommand.ExecuteAsync(null);

        var plan = Assert.Single(gateway.Submitted);
        Assert.Equal(@"D:\Games", plan.InstanceRoot);
        Assert.Equal(Path.Combine(@"D:\Games", "Sunday-survival"), reviewed);
    }

    [Fact]
    public async Task A_blocked_folder_stops_creation_with_the_policy_wording()
    {
        var gateway = new LocationGateway
        {
            Refusal = new VanillaDestinationPreview
            {
                ServerName = "Sunday survival",
                InstanceRoot = @"D:\Games",
                CanonicalDestination = @"D:\Games\Sunday-survival",
                Verdict = CreationDestinationVerdict.BlockedNotEmpty,
                IsAvailable = false,
                Message = "That folder already has files in it. Nothing was changed."
            }
        };
        var model = await SetupAsync(gateway, new FakeChooser { Result = @"D:\Games" });
        await SetNameAsync(model, "Sunday survival");
        model.SelectedVersion = model.Versions.First();

        Assert.True(model.HasLocationMessages);
        Assert.Contains(model.LocationMessages, message =>
            message.Contains("already has files", StringComparison.OrdinalIgnoreCase));
        Assert.False(model.NextCommand.CanExecute(null));
        Assert.Equal("Blocked", LiveVanillaReviewBuilder.DescribeDestination(model.Destination));
    }

    [Fact]
    public void Audit_remediation_keeps_validation_and_navigation_at_the_point_of_use()
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml.cs"));

        Assert.Contains("HasLocationMessages", xaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"This location cannot be used\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HasNameMessages", xaml, StringComparison.Ordinal);
        Assert.Contains("AppDanger", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WizardScroller\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ReviewHeading\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WizardScroller.ScrollToTop()", code, StringComparison.Ordinal);
        Assert.Contains("CreationWizardStep.Review => ReviewHeading", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CreationWizardStep.Review => EulaBox", code, StringComparison.Ordinal);
        Assert.True(code.IndexOf("ScrollToTop()", StringComparison.Ordinal) <
                    code.IndexOf("target?.Focus()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Review_and_result_keep_long_paths_copyable_and_legal_state_single_sourced()
    {
        var gateway = new LocationGateway();
        var model = await ReviewAsync(gateway, new FakeChooser { Result = @"D:\Games" });
        var destination = model.Review.Sections.SelectMany(section => section.Rows)
            .Single(row => row.Label == "Destination");
        var xaml = File.ReadAllText(Path.Combine(
            AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml"));

        Assert.True(destination.IsCopyable);
        Assert.DoesNotContain(model.Review.Sections, section => section.Title == "Agreement");
        Assert.Contains("Download and verification details", xaml, StringComparison.Ordinal);
        Assert.Contains("Cancel creation", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CopyValueCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenServerButton.IsVisible ? OpenServerButton : CloseButton",
            File.ReadAllText(Path.Combine(AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_remediation_has_bounded_content_named_compact_identity_and_contextual_actions()
    {
        var overview = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerOverviewPage.xaml"));
        var shell = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.xaml"));

        Assert.Contains("MaxWidth=\"{StaticResource AppMeasureContent}\"", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Start server\"", overview, StringComparison.Ordinal);
        Assert.Contains("Reachability not verified", overview, StringComparison.Ordinal);
        Assert.Contains("Set up protection", overview, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding ActiveServerSummary}\"", shell, StringComparison.Ordinal);
        Assert.Contains("Kind=\"Server\"", shell, StringComparison.Ordinal);
        Assert.Contains("StateToStartVisibility", shell, StringComparison.Ordinal);
        Assert.Contains("StateToStopVisibility", shell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_location_withdraws_an_acceptance_given_for_the_previous_plan()
    {
        var gateway = new LocationGateway();
        var model = await ReviewAsync(gateway, new FakeChooser { Result = @"D:\Games" });
        model.EulaAccepted = true;

        await model.ChooseLocationCommand.ExecuteAsync(null);

        Assert.False(model.EulaAccepted);
        Assert.False(model.CreateCommand.CanExecute(null));
    }

    [Fact]
    public void The_location_action_is_hidden_when_nothing_can_present_a_folder_picker()
    {
        Assert.False(new LiveVanillaWizardViewModel(new LocationGateway()).CanChooseLocation);
        Assert.True(new LiveVanillaWizardViewModel(
            new LocationGateway(), locationChooser: new FakeChooser()).CanChooseLocation);
    }

    [Fact]
    public void Location_copy_is_compact_and_truthful_before_a_name_is_valid()
    {
        var model = new LiveVanillaWizardViewModel(new LocationGateway(), locationChooser: new FakeChooser());
        var xaml = File.ReadAllText(Path.Combine(
            AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml"));

        // One statement and one path. Two labelled rows - "Folder: …" beside "Location: Default" -
        // read as two unrelated facts about the same thing.
        Assert.Equal("Set once the server has a name", model.DestinationSummary);
        Assert.Equal("Default ChunkPilot folder", model.LocationSummary);
        Assert.Contains("Description=\"{Binding LocationSummary}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ChunkPilot keeps managed servers together", xaml, StringComparison.Ordinal);
        Assert.Contains("Use default", xaml, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ version presentation

    [Theory]
    [InlineData(VanillaReleaseChannel.Stable, "Release")]
    [InlineData(VanillaReleaseChannel.Snapshot, "Snapshot")]
    public void A_release_channel_reads_as_a_channel_not_as_a_finished_release(
        VanillaReleaseChannel channel, string expected)
    {
        Assert.Equal(expected, LiveVanillaReviewBuilder.DescribeChannel(channel));
        Assert.DoesNotContain("Finished", LiveVanillaReviewBuilder.DescribeChannel(channel),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stable_release_reads_as_a_date_and_a_requirement()
    {
        var line = LiveVanillaReviewBuilder.DescribeVersionLine(Version());

        Assert.StartsWith("Released ", line, StringComparison.Ordinal);
        Assert.Contains("Requires Java 25", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Finished release", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_release_date_produces_no_stray_separator()
    {
        var line = LiveVanillaReviewBuilder.DescribeVersionLine(Version() with { ReleaseTime = null });

        Assert.Equal("Requires Java 25", line);
        Assert.DoesNotContain("·", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unestablished_java_requirement_stays_unknown_rather_than_being_guessed()
    {
        var line = LiveVanillaReviewBuilder.DescribeVersionLine(
            Version() with { RequiredJavaMajor = null, JavaRequirementSource = JavaRequirementSource.Unknown });

        Assert.Contains("Java requirement unknown", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Java 2", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snapshot_is_labelled_as_one()
    {
        var line = LiveVanillaReviewBuilder.DescribeVersionLine(
            Version() with { Channel = VanillaReleaseChannel.Snapshot });

        Assert.Contains("Snapshot", line, StringComparison.Ordinal);
        Assert.StartsWith("Published ", line, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ console

    [Fact]
    public async Task Enter_sends_the_command_through_the_same_command_as_the_button()
    {
        var page = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerConsolePage.xaml"));
        var root = XDocument.Parse(page).Root!;
        var binding = root.Descendants()
            .First(element => element.Name.LocalName == "KeyBinding");

        Assert.Equal("Return", binding.Attribute("Key")?.Value);
        Assert.Contains("SendConsoleCommandCommand", binding.Attribute("Command")?.Value ?? "",
            StringComparison.Ordinal);

        // And that command is the one a disabled state governs.
        var model = await ConsoleModelAsync();
        Assert.False(model.SendConsoleCommandCommand.CanExecute(null));
        model.ConsoleCommand = "list";
        Assert.True(model.SendConsoleCommandCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_empty_or_whitespace_command_is_never_sent()
    {
        var model = await ConsoleModelAsync();

        foreach (var text in new[] { "", "   ", "\t" })
        {
            model.ConsoleCommand = text;
            Assert.False(model.CanSendConsoleCommand);
            await model.SendConsoleCommandCommand.ExecuteAsync(null);
        }

        Assert.Equal(0, ConsoleClient(model).SentCommands);
    }

    [Fact]
    public async Task A_successful_send_clears_the_box()
    {
        var model = await ConsoleModelAsync();
        model.ConsoleCommand = "list";

        await model.SendConsoleCommandCommand.ExecuteAsync(null);

        Assert.Equal("", model.ConsoleCommand);
        Assert.Equal(1, ConsoleClient(model).SentCommands);
    }

    [Fact]
    public async Task A_refused_send_keeps_the_command_for_another_try()
    {
        var client = new ConsoleAgentClient { CommandSucceeds = false };
        var model = await ConsoleModelAsync(client);
        model.ConsoleCommand = "list";

        await model.SendConsoleCommandCommand.ExecuteAsync(null);

        Assert.Equal("list", model.ConsoleCommand);
        Assert.Equal(1, client.SentCommands);
    }

    [Fact]
    public async Task Scrolling_away_from_the_bottom_pauses_follow_and_returning_resumes_it()
    {
        var model = await ConsoleModelAsync();
        Assert.True(model.IsConsoleFollowing);

        model.SetConsoleViewport(false);
        Assert.False(model.IsConsoleFollowing);
        Assert.Contains("Paused", model.ConsoleFollowStateText, StringComparison.Ordinal);

        model.SetConsoleViewport(true);
        Assert.True(model.IsConsoleFollowing);
        Assert.Contains("Following", model.ConsoleFollowStateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jumping_to_latest_resumes_follow_and_clears_the_unseen_count()
    {
        var model = await ConsoleModelAsync();
        model.SetConsoleViewport(false);

        model.JumpToLatestCommand.Execute(null);

        Assert.True(model.IsConsoleFollowing);
        Assert.Equal(0, model.UnseenConsoleLines);
    }

    [Fact]
    public void The_console_page_reports_its_viewport_so_follow_can_pause()
    {
        // The handler existed on the shell but nothing ever raised it, so follow never paused. The
        // page now owns that reporting; this pins the wiring rather than the arithmetic.
        var code = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerConsolePage.xaml.cs"));

        Assert.Contains("consoleScroller.ScrollChanged += OnConsoleScrollChanged", code, StringComparison.Ordinal);
        Assert.Contains("SetConsoleViewport", code, StringComparison.Ordinal);
        Assert.Contains("consoleScroller.ScrollableHeight", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rendered_console_scrollviewer_pauses_and_resumes_follow()
    {
        var client = new ConsoleAgentClient
        {
            WithConsole = true,
            ConsoleLineCount = 200
        };
        var model = await ConsoleModelAsync(client, withConsole: true);
        Assert.Equal(200, model.ConsoleLines.Count);

        WpfDesignSystemHost.Run(() =>
        {
            var converterKeys = InstallApplicationConverters();
            MainWindow? window = null;
            try
            {
                window = new MainWindow(model, new AgentClient())
                {
                    Width = 1000,
                    Height = 700,
                    ShowInTaskbar = false
                };
                model.Navigation.NavigateServer(
                    ChunkPilot.App.Navigation.ServerDestination.Console,
                    model.SelectedServer!.Definition.Id);
                typeof(MainWindow)
                    .GetMethod("UpdateServerPageContent",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(window, null);
                window.Show();
                window.UpdateLayout();

                var shellScroller = Assert.IsType<ScrollViewer>(window.FindName("PageScroller"));
                Assert.Equal(ScrollBarVisibility.Disabled, shellScroller.VerticalScrollBarVisibility);
                var host = Assert.IsType<ContentControl>(window.FindName("ServerPageHost"));
                var page = Assert.IsType<ServerConsolePage>(host.Content);
                var scroller = Assert.Single(VisualDescendants<ScrollViewer>(page.ConsoleListBox));
                Assert.True(scroller.ScrollableHeight > 0,
                    $"List items={page.ConsoleListBox.Items.Count}, actual height={page.ConsoleListBox.ActualHeight}, " +
                    $"extent={scroller.ExtentHeight}, viewport={scroller.ViewportHeight}.");
                scroller.ScrollToEnd();
                window.Dispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background, () => { });
                Assert.True(model.IsConsoleFollowing);

                scroller.ScrollToTop();
                window.Dispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background, () => { });
                Assert.False(model.IsConsoleFollowing);
                Assert.Contains("Paused", model.ConsoleFollowStateText, StringComparison.Ordinal);

                scroller.ScrollToEnd();
                window.Dispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background, () => { });
                Assert.True(model.IsConsoleFollowing);
            }
            finally
            {
                window?.Close();
                RemoveApplicationConverters(converterKeys);
            }
        });
    }

    [Theory]
    [InlineData(ServerState.Stopped, false)]
    [InlineData(ServerState.Running, false)]
    [InlineData(ServerState.Running, true)]
    public async Task Shell_navigation_realises_console_for_stopped_running_empty_and_existing_output(
        ServerState state,
        bool withConsole)
    {
        var model = await ConsoleModelAsync(state: state, withConsole: withConsole);

        WpfDesignSystemHost.Run(() =>
        {
            var converterKeys = InstallApplicationConverters();
            MainWindow? window = null;
            try
            {
                window = new MainWindow(model, new AgentClient());
                model.Navigation.NavigateServer(
                    ChunkPilot.App.Navigation.ServerDestination.Console,
                    model.SelectedServer!.Definition.Id);
                typeof(MainWindow)
                    .GetMethod("UpdateServerPageContent",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(window, null);
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });
                var host = Assert.IsType<ContentControl>(window.FindName("ServerPageHost"));
                var page = Assert.IsType<ServerConsolePage>(host.Content);
                page.Measure(new Size(1000, 700));
                page.Arrange(new Rect(0, 0, 1000, 700));
                page.UpdateLayout();
                Assert.Equal(withConsole ? 1 : 0, page.ConsoleListBox.Items.Count);
            }
            finally
            {
                window?.Close();
                RemoveApplicationConverters(converterKeys);
            }
        });
    }

    [Fact]
    public void The_console_page_owns_one_scroll_region_and_uses_shared_controls()
    {
        var root = XDocument.Load(Path.Combine(AppDirectory, "Pages", "ServerConsolePage.xaml")).Root!;

        Assert.DoesNotContain(root.DescendantsAndSelf(), element => element.Name.LocalName == "ScrollViewer");
        var literalMargins = root.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "Margin" or "Padding")
            .Where(attribute => !attribute.Value.StartsWith('{'))
            .ToArray();
        Assert.True(literalMargins.Length == 0,
            "Literal spacing: " + string.Join(", ", literalMargins.Select(a => a.Value)));
    }

    // ------------------------------------------------------------------ shared visual contracts

    [Fact]
    public void The_scroll_bar_target_is_wider_than_the_pill_it_draws()
    {
        var metrics = XDocument.Load(Path.Combine(
            DesignSystemFiles.ThemesDirectory, "Tokens", "MetricTokens.xaml")).Root!;

        var track = Value(metrics, "AppScrollBarThickness");
        var pill = Value(metrics, "AppScrollBarThumbThickness");

        Assert.True(pill < track, "The pill must stay narrower than the target it sits in.");
        Assert.True(track - pill >= 6, "The target must extend several pixels past the pill.");
        Assert.True(pill >= 3, "The pill must remain visible.");
    }

    [Fact]
    public void The_scroll_bar_thumb_hit_area_fills_the_thumb()
    {
        var scrollBars = XDocument.Load(Path.Combine(
            DesignSystemFiles.ThemesDirectory, "Controls", "ScrollBars.xaml")).Root!;

        // Every thumb template's outermost element carries a brush, because a null background does
        // not hit-test and the drag target would collapse back onto the pill.
        var templates = scrollBars.Descendants()
            .Where(element => element.Name.LocalName == "ControlTemplate")
            .Where(element => element.Attribute("TargetType")?.Value == "Thumb")
            .ToArray();

        Assert.NotEmpty(templates);
        Assert.All(templates, template =>
        {
            var host = template.Elements().First();
            Assert.Contains("AppSurfaceTransparent", host.Attribute("Background")?.Value ?? "",
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_text_box_content_host_binds_its_alignment_and_scroll_visibility()
    {
        var inputs = File.ReadAllText(Path.Combine(
            DesignSystemFiles.ThemesDirectory, "Controls", "Inputs.xaml"));
        var host = XDocument.Parse(inputs).Root!
            .Descendants()
            .First(element => element.Name.LocalName == "ScrollViewer" &&
                              element.Attributes().Any(a => a.Value == "PART_ContentHost") &&
                              element.Attribute("VerticalContentAlignment") is not null);

        Assert.Equal("{TemplateBinding VerticalContentAlignment}", host.Attribute("VerticalContentAlignment")?.Value);
        Assert.Equal("{TemplateBinding VerticalScrollBarVisibility}",
            host.Attribute("VerticalScrollBarVisibility")?.Value);
        // A fixed Auto here is what reserved a scroll bar inside a single-line field and pushed the
        // text away from the caret.
        Assert.DoesNotContain("VerticalScrollBarVisibility=\"Auto\"", inputs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shared_text_box_caret_and_hit_testing_follow_the_rendered_text()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var box = new TextBox { Width = 520, Text = "Sunday survival" };
            var window = new Window { Width = 620, Height = 180, Content = box, ShowInTaskbar = false };
            try
            {
                window.Show();
                box.Focus();
                box.CaretIndex = box.Text.Length;
                box.UpdateLayout();

                var start = box.GetRectFromCharacterIndex(0, true);
                var end = box.GetRectFromCharacterIndex(box.Text.Length, true);
                Assert.True(end.Left > start.Left, $"Caret did not advance: {start.Left} -> {end.Left}");

                for (var index = 0; index <= box.Text.Length; index++)
                {
                    var boundary = box.GetRectFromCharacterIndex(index, true);
                    var hit = box.GetCharacterIndexFromPoint(
                        new Point(boundary.Left, boundary.Top + Math.Max(1, boundary.Height / 2)), true);
                    Assert.InRange(hit, Math.Max(0, index - 1), Math.Min(box.Text.Length, index + 1));
                }

                box.CaretIndex = 0;
                Assert.Equal(start.Left, box.GetRectFromCharacterIndex(box.CaretIndex, true).Left, 3);
                box.CaretIndex = box.Text.Length;
                Assert.Equal(end.Left, box.GetRectFromCharacterIndex(box.CaretIndex, true).Left, 3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_overview_uses_shared_components_and_no_bare_card_borders()
    {
        var root = XDocument.Load(Path.Combine(AppDirectory, "Pages", "ServerOverviewPage.xaml")).Root!;

        var bareCards = root.DescendantsAndSelf()
            .Where(element => element.Name.LocalName == "Border")
            .Where(element => (element.Attribute("Style")?.Value ?? "").Contains("AppCard", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(bareCards);
        Assert.Contains(root.Descendants(), element => element.Name.LocalName == "AppSectionCard");
        Assert.DoesNotContain(root.Descendants(), element =>
            element.Name.LocalName == "AppSectionCard" &&
            element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "AppSectionCard"));

        // No inline foreground overrides: they are how a value ends up a different colour from the
        // style that was supposed to govern it.
        var inlineForegrounds = root.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName == "Foreground")
            .ToArray();
        Assert.True(inlineForegrounds.Length == 0,
            "Inline foregrounds: " + string.Join(", ", inlineForegrounds.Select(a => a.Value)));
    }

    [Fact]
    public async Task Overview_identity_and_section_headings_resolve_to_readable_semantic_foregrounds()
    {
        var model = await ConsoleModelAsync(state: ServerState.Stopped);

        WpfDesignSystemHost.Run(() =>
        {
            var converterKeys = InstallApplicationConverters();
            MainWindow? window = null;
            try
            {
                window = new MainWindow(model, new AgentClient()) { ShowInTaskbar = false };
                model.Navigation.NavigateServer(
                    ChunkPilot.App.Navigation.ServerDestination.Overview,
                    model.SelectedServer!.Definition.Id);
                typeof(MainWindow)
                    .GetMethod("UpdateServerPageContent",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(window, null);
                window.Show();
                window.Measure(new Size(1440, 900));
                window.Arrange(new Rect(0, 0, 1440, 900));
                window.UpdateLayout();
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });

                var expected = Assert.IsType<SolidColorBrush>(
                    WpfDesignSystemHost.Resolve("AppTextPrimary"));
                Assert.Equal(expected.Color, Assert.IsType<SolidColorBrush>(window.Foreground).Color);

        string[] required = ["Fixture", "At a glance", "Connect", "Protection", "Files"];
                var relevant = VisualDescendants<TextBlock>(window)
                    .Where(text => required.Contains(text.Text, StringComparer.Ordinal))
                    .ToArray();
                Assert.Equal(required.Length, relevant.Select(text => text.Text).Distinct().Count());
                Assert.All(relevant, text =>
                {
                    var color = Assert.IsType<SolidColorBrush>(text.Foreground).Color;
                    Assert.True(Math.Max(color.R, Math.Max(color.G, color.B)) > 96,
                        $"{text.Text} resolved to unreadably dark {color}.");
                });
            }
            finally
            {
                window?.Close();
                RemoveApplicationConverters(converterKeys);
            }
        });
    }

    [Fact]
    public void Primary_text_styles_explicitly_resolve_the_semantic_foreground()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var expected = Assert.IsType<SolidColorBrush>(
                WpfDesignSystemHost.Resolve("AppTextPrimary"));
            string[] primaryStyles =
            [
                "AppDisplayText",
                "AppNumericText",
                "AppTitleLargeText",
                "AppTitleText",
                "AppSubtitleText",
                "AppBodyText",
                "AppMonoText"
            ];

            foreach (var key in primaryStyles)
            {
                var text = new TextBlock
                {
                    Style = Assert.IsType<Style>(WpfDesignSystemHost.Resolve(key))
                };
                Assert.Equal(expected.Color, Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            }
        });
    }

    [Fact]
    public async Task A_stopped_server_explains_the_absent_metrics_instead_of_showing_blank_cards()
    {
        var model = await ConsoleModelAsync(state: ServerState.Stopped);

        Assert.False(model.ShowsRuntimeMetrics);
        Assert.True(model.ShowsStoppedSummary);
        Assert.Contains("only while the server runs", model.StoppedSummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_running_server_with_a_sample_shows_the_measured_values()
    {
        var model = await ConsoleModelAsync(state: ServerState.Running, withStatistics: true);

        Assert.True(model.ShowsRuntimeMetrics);
        Assert.False(model.ShowsStoppedSummary);
        Assert.False(model.ShowsPerformanceCharts);
    }

    [Fact]
    public async Task A_running_server_without_a_sample_never_claims_a_measurement()
    {
        var model = await ConsoleModelAsync(state: ServerState.Running, withStatistics: false);

        Assert.False(model.ShowsRuntimeMetrics);
        Assert.Contains("Waiting for the first sample", model.StoppedSummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Charts_require_two_real_samples_and_never_synthesize_history()
    {
        var noSamples = await ConsoleModelAsync(state: ServerState.Running, withStatistics: false);
        var oneSample = await ConsoleModelAsync(state: ServerState.Running, withStatistics: true);
        var history = await ConsoleModelAsync(
            state: ServerState.Running, withStatistics: true, statisticCount: 3);

        Assert.False(noSamples.ShowsPerformanceCharts);
        Assert.False(oneSample.ShowsPerformanceCharts);
        Assert.True(history.ShowsPerformanceCharts);
        Assert.Equal(3, history.SelectedServer!.RecentStatistics.Count);
    }

    [Fact]
    public void Chart_summaries_report_real_current_average_peak_units_and_window()
    {
        var origin = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        StatisticsSample[] samples =
        [
            new() { Timestamp = origin, CpuPercent = 10, WorkingSetBytes = 1L << 30 },
            new() { Timestamp = origin.AddSeconds(30), CpuPercent = 20, WorkingSetBytes = 2L << 30 },
            new() { Timestamp = origin.AddSeconds(60), CpuPercent = 30, WorkingSetBytes = 3L << 30 }
        ];

        var cpu = SparklineControl.Summarize(samples, "Cpu");
        var memory = SparklineControl.Summarize(samples, "Ram", 4096);

        Assert.Equal("30.0%", cpu.CurrentText);
        Assert.Equal("20.0%", cpu.AverageText);
        Assert.Equal("30.0%", cpu.PeakText);
        Assert.Contains("3 real samples", cpu.WindowText, StringComparison.Ordinal);
        Assert.Equal("Scale 0–100%", cpu.ScaleText);
        Assert.Equal("3.0 GB", memory.CurrentText);
        Assert.Equal("2.0 GB", memory.AverageText);
        Assert.Equal("3.0 GB", memory.PeakText);
        Assert.Contains("4.0 GB configured", memory.ScaleText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_information_card_is_absent()
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml"));

        Assert.DoesNotContain("Creates a real server", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Continuing downloads Mojang", xaml, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ launch

    [Fact]
    public void The_live_wizard_is_raised_once_and_is_never_left_pinned_above_other_windows()
    {
        var code = File.ReadAllText(Path.Combine(
            AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml.cs"));

        Assert.Contains("PresentInForeground", code, StringComparison.Ordinal);
        var presenter = File.ReadAllText(Path.Combine(AppDirectory, "WindowForegroundPresenter.cs"));
        Assert.Contains("SetForegroundWindow", presenter, StringComparison.Ordinal);
        Assert.Contains("IsIconic", presenter, StringComparison.Ordinal);
        Assert.Contains("ShowWindow(handle, RestoreWindow)", presenter, StringComparison.Ordinal);
        // Raised by a toggle that restores whatever the window had, and guarded so it happens once.
        Assert.Contains("WindowForegroundPresenter.Present(this)", code, StringComparison.Ordinal);
        Assert.Contains("window.Topmost = wasTopmost", presenter, StringComparison.Ordinal);
        Assert.Contains("if (presented)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", code, StringComparison.Ordinal);

        var xaml = File.ReadAllText(Path.Combine(
            AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml"));
        Assert.DoesNotContain("Topmost=\"True\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_server_restores_the_shell_and_selects_the_exact_identity_on_overview()
    {
        var target = Snapshot(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Same name");
        var other = Snapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Same name");
        var model = new MainViewModel(new MultiServerClient(other, target), new SilentDialogs());
        await model.InitializeAsync();

        WpfDesignSystemHost.Run(() =>
        {
            var converterKeys = InstallApplicationConverters();
            MainWindow? window = null;
            try
            {
                window = new MainWindow(model, new AgentClient()) { ShowInTaskbar = false };
                window.WindowState = WindowState.Minimized;
                var navigator = new ShellCreatedServerNavigator(model, window);
                navigator.OpenAsync(target.Definition.Id).GetAwaiter().GetResult();
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, () => { });

                Assert.True(window.IsVisible);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(target.Definition.Id, model.SelectedServer!.Definition.Id);
                Assert.Equal(ChunkPilot.App.Navigation.ServerDestination.Overview,
                    model.Navigation.CurrentServerDestination);
                Assert.False(window.Topmost);
            }
            finally
            {
                window?.Close();
                RemoveApplicationConverters(converterKeys);
            }
        });
    }

    [Fact]
    public void Normal_startup_and_the_synthetic_preview_are_untouched_by_the_foreground_change()
    {
        var startup = File.ReadAllText(Path.Combine(AppDirectory, "App.xaml.cs"));

        Assert.Contains("CreateServerPreviewLauncher.TryRun", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("PresentInForeground", startup, StringComparison.Ordinal);
        // The shell is still shown and activated the way it always was.
        Assert.Contains("window.Show();", startup, StringComparison.Ordinal);
        Assert.Contains("window.Activate();", startup, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static double Value(XElement metrics, string key) =>
        double.Parse(metrics.Elements()
            .First(element => element.Attribute(DesignSystemFiles.XamlNamespace + "Key")?.Value == key).Value,
            System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in VisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private static string[] InstallApplicationConverters()
    {
        var resources = Application.Current.Resources;
        var converters = new Dictionary<string, object>
        {
            ["BoolVisibility"] = new ChunkPilot.App.BooleanToVisibilityConverter(),
            ["InverseBoolVisibility"] = new InverseBooleanToVisibilityConverter(),
            ["FalseMeansVisible"] = new InverseBoolToVisibilityConverter(),
            ["PositiveIntVisibility"] = new PositiveIntToVisibilityConverter(),
            ["ZeroIntVisibility"] = new ZeroIntToVisibilityConverter(),
            ["NullVisibility"] = new NullToVisibilityConverter(),
            ["IsNull"] = new IsNullConverter(),
            ["Bytes"] = new BytesConverter(),
            ["StateBrush"] = new StateBrushConverter(),
            ["StateText"] = new ServerStateTextConverter(),
            ["StateTone"] = new ServerStateToneConverter(),
            ["EcosystemSectionName"] = new EcosystemSectionNameConverter(),
            ["ActivityAction"] = new ActivityActionConverter(),
            ["ActivityResultTone"] = new ActivityResultToneConverter(),
            ["StateToStartVisibility"] = new StateToStartVisibilityConverter(),
            ["StateToStopVisibility"] = new StateToStopVisibilityConverter(),
            ["ServerEquals"] = new ServerEqualsConverter()
        };
        var added = converters.Keys.Where(key => !resources.Contains(key)).ToArray();
        foreach (var key in added)
            resources[key] = converters[key];
        return added;
    }

    private static void RemoveApplicationConverters(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            Application.Current.Resources.Remove(key);
    }

    private static VanillaVersionOption Version() => new()
    {
        VersionId = "26.2",
        Channel = VanillaReleaseChannel.Stable,
        ReleaseType = "release",
        ReleaseTime = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero),
        MetadataUrl = "https://piston-meta.mojang.com/v1/packages/fixture/26.2.json",
        HasServerDownload = true,
        ServerDownloadUrl = "https://piston-data.mojang.com/v1/objects/fixture/server.jar",
        ServerSha1 = "823e2250d24b3ddac457a60c92a6a941943fcd6a",
        ServerSizeBytes = 60_894_273,
        RequiredJavaMajor = 25,
        JavaRequirementSource = JavaRequirementSource.OfficialMetadata,
        Support = VanillaVersionSupport.Supported,
        Provenance = "Official Mojang version metadata"
    };

    private static async Task<LiveVanillaWizardViewModel> SetupAsync(
        LocationGateway gateway, IServerLocationChooser? chooser = null)
    {
        var model = new LiveVanillaWizardViewModel(
            gateway, locationChooser: chooser, pollInterval: TimeSpan.FromMilliseconds(1));
        model.SelectedIntent = model.Intents.First(intent => intent.IsLive);
        await model.NextCommand.ExecuteAsync(null);
        return model;
    }

    private static async Task<LiveVanillaWizardViewModel> ReviewAsync(
        LocationGateway gateway, IServerLocationChooser? chooser = null, bool chooseLocation = false)
    {
        var model = await SetupAsync(gateway, chooser);
        await SetNameAsync(model, "Sunday survival");
        if (chooseLocation)
            await model.ChooseLocationCommand.ExecuteAsync(null);
        model.SelectedVersion = model.Versions.First();
        await model.NextCommand.ExecuteAsync(null);
        return model;
    }

    /// <summary>
    /// Sets the name and waits for the destination lookup the view model starts on its own.
    /// </summary>
    /// <remarks>
    /// Waits for the result rather than for a fixed number of yields. A yield count is a guess about
    /// scheduling, and under load it is the wrong guess: the lookup had not finished, so the assertions
    /// that follow read a half-built state. A name with a blocking problem never starts a lookup at
    /// all, so the wait is bounded and simply expires.
    /// </remarks>
    private static async Task SetNameAsync(LiveVanillaWizardViewModel model, string name)
    {
        model.ServerName = name;
        for (var pass = 0; pass < 200 && model.Destination is null; pass++)
            await Task.Delay(5);
        for (var pass = 0; pass < 4; pass++)
            await Task.Yield();
    }

    private static async Task<MainViewModel> ConsoleModelAsync(
        ConsoleAgentClient? client = null,
        ServerState state = ServerState.Running,
        bool withStatistics = true,
        bool withConsole = false,
        int statisticCount = 1)
    {
        client ??= new ConsoleAgentClient();
        client.State = state;
        client.WithStatistics = withStatistics;
        client.WithConsole = withConsole;
        client.StatisticCount = statisticCount;
        var model = new MainViewModel(client, new SilentDialogs());
        await model.InitializeAsync();
        model.SelectedServer = model.Servers.First();
        return model;
    }

    private static ConsoleAgentClient ConsoleClient(MainViewModel model) =>
        (ConsoleAgentClient)typeof(MainViewModel)
            .GetField("client", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(model)!;

    private sealed class FakeChooser : IServerLocationChooser
    {
        public string? Result { get; set; }

        public string? Choose(string title, string startingPath) => Result;
    }

    /// <summary>A gateway that answers destination questions from whatever root it is given.</summary>
    private sealed class LocationGateway : IVanillaCreationGateway
    {
        public const string ManagedRoot = @"C:\Users\Test\ChunkPilot\Servers";

        public string LastInstanceRoot { get; private set; } = "";

        public VanillaDestinationPreview? Refusal { get; set; }

        public List<VanillaCreationPlan> Submitted { get; } = [];

        public Task<VanillaVersionCatalog> GetCatalogAsync(
            bool includeSnapshots, bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new VanillaVersionCatalog
            {
                Options = [Version()],
                RetrievedUtc = DateTimeOffset.UtcNow,
                ProviderAvailable = true
            });

        public async Task<VanillaDestinationPreview> PreviewDestinationAsync(
            string serverName, string instanceRoot, CancellationToken cancellationToken)
        {
            LastInstanceRoot = instanceRoot;
            await Task.Yield();
            if (Refusal is not null)
                return Refusal with { ServerName = serverName };
            var root = instanceRoot.Length > 0 ? instanceRoot : ManagedRoot;
            var folder = serverName.Trim().Replace(' ', '-');
            return new VanillaDestinationPreview
            {
                ServerName = serverName,
                FolderName = folder,
                InstanceRoot = root,
                CanonicalDestination = Path.Combine(root, folder),
                Verdict = CreationDestinationVerdict.Available,
                IsAvailable = true
            };
        }

        public Task<Guid> BeginAsync(VanillaCreationPlan plan, CancellationToken cancellationToken)
        {
            Submitted.Add(plan);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<InstallOperationSnapshot> GetSnapshotAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(new InstallOperationSnapshot
            {
                OperationId = operationId,
                IsTerminal = true,
                Success = true,
                Outcome = CreationOutcome.Completed,
                Progress = new InstallProgress { Stage = CreationStage.Completed }
            });

        public Task CancelAsync(Guid operationId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<InstallOperationSnapshot>> GetCreationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstallOperationSnapshot>>([]);

        public Task<IReadOnlyList<ManagedJavaRuntime>> GetManagedRuntimesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ManagedJavaRuntime>>([]);
    }

    /// <summary>The smallest agent that can answer a dashboard and accept a console command.</summary>
    private sealed class ConsoleAgentClient : IAgentClient
    {
        public ServerState State { get; set; } = ServerState.Running;

        public bool WithStatistics { get; set; } = true;

        public bool WithConsole { get; set; }

        public int ConsoleLineCount { get; set; } = 1;

        public int StatisticCount { get; set; } = 1;

        public bool CommandSucceeds { get; set; } = true;

        public int SentCommands { get; private set; }

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            if (operation == "SendCommand")
                SentCommands++;
            object? response = operation switch
            {
                "Dashboard" => new DashboardSnapshot { AgentConnected = true, Servers = [Snapshot()] },
                "GetSetting" => new TextResponse(""),
                "SendCommand" => new OperationResult
                {
                    Success = CommandSucceeds,
                    Message = CommandSucceeds ? "Sent." : "Server is not running."
                },
                _ => Fallback<TResponse>()
            };
            // A response this fixture has no opinion about is reported as a failed request rather
            // than fabricated: the view model already handles that, and inventing a record would
            // make the test depend on shapes it does not care about.
            return response is null
                ? Task.FromException<TResponse>(new IOException($"No fixture response for {operation}."))
                : Task.FromResult((TResponse)response);
        }

        private ServerSnapshot Snapshot() => new()
        {
            Definition = new ServerDefinition
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Fixture",
                RootPath = @"C:\Fixture",
                MinecraftVersion = "26.2",
                Ecosystem = ServerEcosystem.Vanilla,
                IsManaged = true
            },
            State = State,
            CurrentStatistics = WithStatistics ? new StatisticsSample { CpuPercent = 3.5 } : null,
            RecentStatistics = WithStatistics
                ? Enumerable.Range(0, StatisticCount)
                    .Select(index => new StatisticsSample
                    {
                        Timestamp = DateTimeOffset.UtcNow.AddSeconds(index - StatisticCount + 1),
                        CpuPercent = 3.5 + index,
                        WorkingSetBytes = (256L + index) * 1024 * 1024
                    }).ToArray()
                : [],
            Console = WithConsole
                ? Enumerable.Range(1, ConsoleLineCount)
                    .Select(index => new ConsoleLine(
                        index,
                        DateTimeOffset.UtcNow.AddMilliseconds(index),
                        "stdout",
                        $"Line {index}"))
                    .ToArray()
                : []
        };

        private static object? Fallback<TResponse>() =>
            typeof(TResponse).IsArray
                ? Array.CreateInstance(typeof(TResponse).GetElementType()!, 0)
                : null;
    }

    private static ServerSnapshot Snapshot(Guid id, string name) => new()
    {
        Definition = new ServerDefinition
        {
            Id = id,
            Name = name,
            RootPath = Path.Combine(@"C:\Fixture", id.ToString("N")),
            MinecraftVersion = "26.2",
            Ecosystem = ServerEcosystem.Vanilla,
            IsManaged = true
        },
        State = ServerState.Stopped
    };

    private sealed class MultiServerClient(params ServerSnapshot[] servers) : IAgentClient
    {
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            object? response = operation switch
            {
                "Dashboard" => new DashboardSnapshot { AgentConnected = true, Servers = servers },
                "GetSetting" => new TextResponse(""),
                _ => null
            };
            return response is null
                ? Task.FromException<TResponse>(new IOException($"No fixture response for {operation}."))
                : Task.FromResult((TResponse)response);
        }
    }

    private sealed class SilentDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
