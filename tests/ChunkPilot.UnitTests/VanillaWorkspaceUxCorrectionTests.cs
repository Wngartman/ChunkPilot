using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using ChunkPilot.App;
using ChunkPilot.App.Access;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Navigation;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>
/// The acceptance defects the Vanilla workspace UX correction pass removed.
/// </summary>
/// <remarks>
/// Each test names what the user saw: a caret indented away from the edge, a switch drawn as a
/// page-wide ellipse, a wheel that stopped over an empty table, two pages editing one file, a
/// Console filling with refused commands, a highlight that stayed on Dashboard.
/// </remarks>
public sealed class VanillaWorkspaceUxCorrectionTests
{
    private static readonly string AppDirectory = DesignSystemFiles.AppProjectDirectory;
    private static readonly Guid ServerId = Guid.Parse("8c2d4e10-0000-4000-8000-000000000042");

    private static readonly string[] ManageSectionOrder =
        ["CONFIGURATION", "WORLDS", "MODS & PACKS", "VERSION & UPDATES", "FILES"];

    private static readonly string[] DifficultyLabels = ["Peaceful", "Easy", "Normal", "Hard"];

    private static readonly string[] GameModeLabels = ["Survival", "Creative", "Adventure", "Spectator"];

    // ═════════════════════════════════════════════════════════ 1. text-field leading inset

    /// <summary>Text entry has its own inset token, and it is smaller than a button's padding.</summary>
    [Fact]
    public void The_text_field_inset_is_its_own_token_and_is_compact()
    {
        var padding = Assert.IsType<Thickness>(WpfDesignSystemHost.Resolve("AppTextFieldPadding"));
        Assert.Equal(8, padding.Left);
        Assert.Equal(8, padding.Right);

        var control = Assert.IsType<Thickness>(WpfDesignSystemHost.Resolve("AppControlPadding"));
        Assert.True(padding.Left < control.Left,
            "A text field's leading inset must be tighter than a button's label padding.");
    }

    /// <summary>
    /// The caret, the first typed glyph and the hint all start at the same compact offset.
    /// </summary>
    /// <remarks>
    /// The user's complaint was about the insertion point, not the pointer. The inset is three
    /// layers deep - focus ring, border, padding - and this measures the total the way the user
    /// sees it: the distance from the outside of the control to where text actually begins.
    /// </remarks>
    [Fact]
    public void Typed_text_the_caret_and_the_hint_share_one_compact_origin()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var box = new TextBox { Width = 320 };
            AppInput.SetPlaceholder(box, "Server name");
            var window = new Window { Width = 420, Height = 200, ShowInTaskbar = false, Content = box };
            try
            {
                window.Show();
                window.UpdateLayout();

                var host = FindByName<ScrollViewer>(box, "PART_ContentHost");
                var placeholder = FindByName<TextBlock>(box, "Placeholder");
                Assert.NotNull(host);
                Assert.NotNull(placeholder);

                var textOrigin = host!.TransformToAncestor(box).Transform(new Point(0, 0)).X;
                var hintOrigin = placeholder!.TransformToAncestor(box).Transform(new Point(0, 0)).X;

                // 2px focus ring + 1px border + 8px padding.
                Assert.Equal(11, textOrigin, 1);
                Assert.Equal(textOrigin, hintOrigin, 1);
                // Clear of the border, and nowhere near the 15 the user was shown.
                Assert.InRange(textOrigin, 4, 12);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Selection, Home and End still land where the text is.</summary>
    [Fact]
    public void The_field_still_selects_and_navigates_after_the_inset_change()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var box = new TextBox { Width = 320, Text = "survival" };
            var window = new Window { Width = 420, Height = 200, ShowInTaskbar = false, Content = box };
            try
            {
                window.Show();
                box.Focus();
                box.SelectAll();
                Assert.Equal("survival", box.SelectedText);
                box.CaretIndex = 0;
                Assert.Equal(0, box.CaretIndex);
                box.CaretIndex = box.Text.Length;
                Assert.Equal(8, box.CaretIndex);
                Assert.True(box.ActualHeight >= 32, "The field must not lose its clickable height.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ═════════════════════════════════════════════════════════ 2. Manage ordering

    /// <summary>Configuration leads Manage and Files closes it.</summary>
    [Fact]
    public void Manage_puts_configuration_first_and_files_last()
    {
        // Top-level sections only. The polish pass added GENERAL and WORLD & GAMEPLAY as group
        // headings *inside* the Configuration card; they are not sections of the page.
        var sections = EyebrowSections(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"))
            .Where(section => ManageSectionOrder.Contains(section))
            .ToArray();

        Assert.Equal("CONFIGURATION", sections[0]);
        Assert.Equal("FILES", sections[^1]);
        Assert.Equal(ManageSectionOrder, sections);
    }

    /// <summary>The file editor and its safety wording survived the move.</summary>
    [Fact]
    public void Reordering_manage_kept_the_file_editor_intact()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));

        Assert.Contains("SaveFileCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Save changes", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenSelectedFileCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("EditorStateVisibility", xaml, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════ 3. configuration ownership

    /// <summary>
    /// One page owns server.properties. Settings owns how ChunkPilot runs the server.
    /// </summary>
    [Fact]
    public void Only_manage_edits_server_properties()
    {
        var manage = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));
        var settings = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerSettingsPage.xaml"));

        Assert.Contains("SaveServerPropertiesCommand", manage, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveServerPropertiesCommand", settings, StringComparison.Ordinal);

        foreach (var duplicated in new[]
                 {
                     "PropertyViewDistance", "PropertySimulationDistance", "PropertyPort",
                     "PropertyMaxPlayers", "PropertyOnlineMode"
                 })
        {
            Assert.Contains(duplicated, manage, StringComparison.Ordinal);
            Assert.DoesNotContain(duplicated, settings, StringComparison.Ordinal);
        }

        // Settings keeps what is genuinely its own.
        Assert.Contains("SaveStartupProfileCommand", settings, StringComparison.Ordinal);
        Assert.Contains("MaximumMemoryText", settings, StringComparison.Ordinal);
        Assert.Contains("MemoryPresets", settings, StringComparison.Ordinal);
    }

    /// <summary>Every property the milestone requires has a control, allow-flight included.</summary>
    [Fact]
    public void Configuration_offers_the_settings_people_actually_change()
    {
        var manage = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));

        foreach (var binding in new[]
                 {
                     "PropertyMotd", "PropertyPort", "PropertyMaxPlayers", "PropertyDifficulty",
                     "PropertyGameMode", "PropertyViewDistance", "PropertySimulationDistance",
                     "PropertyAllowFlight", "PropertyPvp", "PropertyOnlineMode", "PropertyWhiteList",
                     "PropertySpawnProtection", "PropertyCommandBlocks", "PropertyHardcore",
                     "PropertyForceGameMode", "PropertyPlayerIdleTimeout"
                 })
            Assert.Contains(binding, manage, StringComparison.Ordinal);
    }

    /// <summary>Raw keys are a tooltip, never the label the beginner reads.</summary>
    [Fact]
    public void Raw_property_keys_are_not_used_as_labels()
    {
        var root = XDocument.Load(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml")).Root!;
        var labels = root.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "Text" or "Content")
            .Select(attribute => attribute.Value)
            .ToArray();

        foreach (var key in new[] { "allow-flight", "white-list", "enable-command-block", "server-port", "motd" })
            Assert.DoesNotContain(key, labels);

        Assert.Contains("Allow flight", labels);
        Assert.Contains("Server message", labels);
    }

    /// <summary>Enum values are shown in title case and stored exactly as the file spells them.</summary>
    [Fact]
    public void Enum_choices_are_title_case_on_screen_and_lower_case_on_disk()
    {
        Assert.Equal(DifficultyLabels, ServerPropertyPresentation.Difficulties.Select(choice => choice.Label));
        Assert.Equal(
            ServerPropertyValidation.Difficulties,
            ServerPropertyPresentation.Difficulties.Select(choice => choice.Value));

        Assert.Equal(GameModeLabels, ServerPropertyPresentation.GameModes.Select(choice => choice.Label));
        Assert.All(ServerPropertyPresentation.GameModes,
            choice => Assert.Equal(choice.Value, choice.Value.ToLowerInvariant()));

        Assert.Equal("Normal", ServerPropertyPresentation.LabelFor(ServerPropertyPresentation.Difficulties, "normal"));
    }

    /// <summary>The dropdowns bind the label for display and the stored value for the file.</summary>
    [Fact]
    public void Configuration_dropdowns_display_labels_and_select_values()
    {
        var manage = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));

        // DisplayMemberPath alone left the closed control rendering the item's ToString, so the
        // polish pass replaced it with the shared choice template. Both bind the label; only one
        // reaches the selection box.
        Assert.Contains("AppPropertyChoiceTemplate", manage, StringComparison.Ordinal);
        Assert.Contains("SelectedValuePath=\"Value\"", manage, StringComparison.Ordinal);
        // The old raw-value lists are gone.
        Assert.DoesNotContain("ItemsSource=\"{Binding Difficulties}\"", manage, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding GameModes}\"", manage, StringComparison.Ordinal);
    }

    /// <summary>Apply is dead until a value genuinely differs, and alive the moment one does.</summary>
    [Fact]
    public async Task Apply_is_disabled_until_a_value_actually_changes()
    {
        using var fixture = new ServerFixture();
        var client = new ConfigurationFakeClient(fixture.RootPath);
        var model = await ReadyModelAsync(client);

        Assert.False(model.HasServerPropertyChanges);
        Assert.False(model.SaveServerPropertiesCommand.CanExecute(null));

        model.PropertyAllowFlight = true;
        Assert.True(model.HasServerPropertyChanges);
        Assert.True(model.SaveServerPropertiesCommand.CanExecute(null));

        // Typed back to what the file says is not a change.
        model.PropertyAllowFlight = false;
        Assert.False(model.HasServerPropertyChanges);
        Assert.False(model.SaveServerPropertiesCommand.CanExecute(null));
    }

    /// <summary>Allow flight goes to the file as the file spells booleans, and comes back.</summary>
    [Fact]
    public async Task Allow_flight_round_trips_through_the_agent_write_path()
    {
        using var fixture = new ServerFixture();
        var client = new ConfigurationFakeClient(fixture.RootPath);
        var model = await ReadyModelAsync(client);

        model.PropertyAllowFlight = true;
        await model.SaveServerPropertiesCommand.ExecuteAsync(null);

        Assert.NotNull(client.LastWrite);
        Assert.Equal("true", client.LastWrite!["allow-flight"]);
        Assert.True(model.PropertyAllowFlight);
        Assert.False(model.HasServerPropertyChanges);
        Assert.Equal("", model.ServerPropertySaveError);
    }

    /// <summary>Difficulty is written lower case even though the control shows title case.</summary>
    [Fact]
    public async Task Difficulty_is_written_in_the_files_own_spelling()
    {
        using var fixture = new ServerFixture();
        var client = new ConfigurationFakeClient(fixture.RootPath);
        var model = await ReadyModelAsync(client);

        model.PropertyDifficulty = "hard";
        await model.SaveServerPropertiesCommand.ExecuteAsync(null);

        Assert.Equal("hard", client.LastWrite!["difficulty"]);
    }

    /// <summary>A refused write keeps the edits and says so inline.</summary>
    [Fact]
    public async Task A_failed_save_preserves_the_edits_and_shows_an_inline_error()
    {
        using var fixture = new ServerFixture();
        var client = new ConfigurationFakeClient(fixture.RootPath) { WriteFailure = "The file is read-only." };
        var model = await ReadyModelAsync(client);

        model.PropertyMaxPlayers = 42;
        await model.SaveServerPropertiesCommand.ExecuteAsync(null);

        Assert.Equal(42, model.PropertyMaxPlayers);
        Assert.True(model.HasServerPropertySaveError);
        Assert.Contains("read-only", model.ServerPropertySaveError, StringComparison.OrdinalIgnoreCase);
        Assert.True(model.HasServerPropertyChanges);
    }

    /// <summary>A background re-read never overwrites what somebody is in the middle of typing.</summary>
    [Fact]
    public async Task A_refresh_does_not_overwrite_unsaved_edits()
    {
        using var fixture = new ServerFixture();
        var client = new ConfigurationFakeClient(fixture.RootPath);
        var model = await ReadyModelAsync(client);

        model.PropertyMotd = "Sunday survival";
        await model.RefreshCommand.ExecuteAsync(null);
        await Settle();

        Assert.Equal("Sunday survival", model.PropertyMotd);
    }

    /// <summary>Out-of-range numbers are refused before anything is written.</summary>
    [Theory]
    [InlineData("view-distance", "99")]
    [InlineData("view-distance", "1")]
    [InlineData("max-players", "0")]
    [InlineData("server-port", "70000")]
    [InlineData("player-idle-timeout", "-1")]
    [InlineData("spawn-protection", "-5")]
    public void Invalid_numeric_values_are_rejected(string key, string value)
    {
        var errors = ServerPropertyValidation.Validate(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value });

        Assert.True(errors.ContainsKey(key), $"{key}={value} should not be accepted.");
    }

    /// <summary>An invalid value stops at the page: it is never handed to the Agent.</summary>
    [Fact]
    public async Task An_invalid_value_is_reported_inline_and_never_written()
    {
        using var fixture = new ServerFixture();
        var client = new ConfigurationFakeClient(fixture.RootPath);
        var model = await ReadyModelAsync(client);

        model.PropertyDifficulty = "brutal";
        await model.SaveServerPropertiesCommand.ExecuteAsync(null);

        Assert.Null(client.LastWrite);
        Assert.True(model.HasServerPropertySaveError);
    }

    /// <summary>A restart-only change is named and offers an explicit save-and-restart action.</summary>
    [Fact]
    public async Task A_restart_only_change_is_named_rather_than_acted_on()
    {
        using var fixture = new ServerFixture();
        var client = new ConfigurationFakeClient(fixture.RootPath);
        var model = await ReadyModelAsync(client);

        model.PropertyPort = 25566;

        Assert.True(model.ShowsRestartRequiredNotice);
        Assert.Contains("server-port", model.RestartRequiredNotice, StringComparison.Ordinal);

        model.PropertyPort = 25565;
        model.PropertyDifficulty = "hard";
        Assert.False(model.ShowsRestartRequiredNotice);

        var page = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));
        Assert.Contains("SaveServerPropertiesAndRestartCommand", page, StringComparison.Ordinal);
        Assert.Contains("RestartServerCommand", page, StringComparison.Ordinal);
    }

    /// <summary>The comment-preserving document keeps unrelated lines untouched.</summary>
    [Fact]
    public void Writing_a_property_leaves_comments_and_other_lines_alone()
    {
        const string original = "#Minecraft server properties\r\nmotd=A Minecraft Server\r\nallow-flight=false\r\npvp=true\r\n";
        var document = ServerPropertiesDocument.Parse(original);
        document.Set("allow-flight", "true");
        var written = document.ToString();

        Assert.Contains("#Minecraft server properties", written, StringComparison.Ordinal);
        Assert.Contains("allow-flight=true", written, StringComparison.Ordinal);
        Assert.Contains("pvp=true", written, StringComparison.Ordinal);
        Assert.Equal("true", ServerPropertiesDocument.Parse(written).Values["allow-flight"]);
    }

    // ═════════════════════════════════════════════════════════ 4. switch visuals

    /// <summary>
    /// The focus ring is the size of the switch, not the size of the row it sits in.
    /// </summary>
    /// <remarks>
    /// This is the whitelist control from the screenshot. Given a 600px-wide cell the old template
    /// stretched its pill-radius ring across the whole of it; the assertion is that widening the
    /// container changes nothing about the ring.
    /// </remarks>
    [Fact]
    public void A_switch_does_not_stretch_with_the_space_it_is_given()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var wide = NewSwitch("Whitelist");
            var narrow = NewSwitch("Whitelist");
            var host = new Grid { Width = 600 };
            var second = new Grid { Width = 200 };
            host.Children.Add(wide);
            second.Children.Add(narrow);
            var stack = new StackPanel();
            stack.Children.Add(host);
            stack.Children.Add(second);
            var window = new Window { Width = 700, Height = 300, ShowInTaskbar = false, Content = stack };
            try
            {
                window.Show();
                window.UpdateLayout();

                var wideRing = FindByName<Border>(wide, "FocusRing")!;
                var narrowRing = FindByName<Border>(narrow, "FocusRing")!;

                Assert.Equal(44, wideRing.ActualWidth, 1);
                Assert.Equal(26, wideRing.ActualHeight, 1);
                Assert.Equal(wideRing.ActualWidth, narrowRing.ActualWidth, 1);
                Assert.True(wide.ActualWidth < 200,
                    "A labelled switch must be as wide as its switch and label, not as wide as its cell.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The pointer target clears the minimum, and the track keeps its own size.</summary>
    [Fact]
    public void The_switch_meets_the_minimum_pointer_target()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var toggle = NewSwitch(null);
            var window = new Window { Width = 400, Height = 200, ShowInTaskbar = false, Content = toggle };
            try
            {
                window.Show();
                window.UpdateLayout();

                var ring = FindByName<Border>(toggle, "FocusRing")!;
                var track = FindByName<Border>(toggle, "Track")!;

                Assert.True(ring.ActualWidth >= 24 && ring.ActualHeight >= 24);
                Assert.Equal(40, track.ActualWidth, 1);
                Assert.Equal(22, track.ActualHeight, 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>On and off differ by knob position, not by colour alone.</summary>
    [Fact]
    public void Checked_and_unchecked_differ_by_more_than_colour()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var toggle = NewSwitch(null);
            var window = new Window { Width = 400, Height = 200, ShowInTaskbar = false, Content = toggle };
            try
            {
                window.Show();
                window.UpdateLayout();
                var knob = FindByName<Border>(toggle, "Knob")!;
                Assert.Equal(HorizontalAlignment.Left, knob.HorizontalAlignment);

                toggle.IsChecked = true;
                window.UpdateLayout();
                Assert.Equal(HorizontalAlignment.Right, knob.HorizontalAlignment);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A switch waiting on the server is visibly not operable.</summary>
    [Fact]
    public void A_pending_switch_is_disabled_and_reads_as_unavailable()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var toggle = NewSwitch("Whitelisted");
            toggle.IsEnabled = false;
            var window = new Window { Width = 400, Height = 200, ShowInTaskbar = false, Content = toggle };
            try
            {
                window.Show();
                window.UpdateLayout();

                var track = FindByName<Border>(toggle, "Track")!;
                Assert.Equal(Application.Current.TryFindResource("AppSurfaceDisabled"), track.Background);
                Assert.Equal(Cursors.Arrow, toggle.Cursor);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The keyboard focus is visible and the standard accessible toggle action works.</summary>
    [Fact]
    public void Keyboard_focus_and_accessible_toggle_are_visible()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var toggle = NewSwitch("Whitelist");
            var window = new Window { Width = 400, Height = 200, ShowInTaskbar = false, Content = toggle };
            try
            {
                window.Show();
                Assert.True(toggle.Focus());
                window.UpdateLayout();

                var ring = FindByName<Border>(toggle, "FocusRing")!;
                Assert.Equal(Application.Current.TryFindResource("AppFocusRing"), ring.BorderBrush);

                Assert.False(toggle.IsChecked);
                var peer = new CheckBoxAutomationPeer(toggle);
                var provider = Assert.IsAssignableFrom<IToggleProvider>(
                    peer.GetPattern(PatternInterface.Toggle));
                provider.Toggle();
                Assert.True(toggle.IsChecked);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ═════════════════════════════════════════════════════════ 5. nested wheel

    /// <summary>A scroller that has nowhere to go in that direction does not claim the wheel.</summary>
    [Fact]
    public void Boundary_detection_answers_for_top_middle_bottom_and_empty()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var empty = Scroller(contentHeight: 40, viewportHeight: 100);
            var list = Scroller(contentHeight: 400, viewportHeight: 100);
            var window = new Window
            {
                Width = 400,
                Height = 300,
                ShowInTaskbar = false,
                Content = new StackPanel { Children = { empty, list } }
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.False(AppScroll.CanScroll(empty, 120));
                Assert.False(AppScroll.CanScroll(empty, -120));

                // At the top: up is refused, down is taken.
                Assert.False(AppScroll.CanScroll(list, 120));
                Assert.True(AppScroll.CanScroll(list, -120));

                list.ScrollToVerticalOffset(150);
                list.UpdateLayout();
                Assert.True(AppScroll.CanScroll(list, 120));
                Assert.True(AppScroll.CanScroll(list, -120));

                list.ScrollToEnd();
                list.UpdateLayout();
                Assert.True(AppScroll.CanScroll(list, 120));
                Assert.False(AppScroll.CanScroll(list, -120));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The wheel over an empty inner table still scrolls the page underneath it.</summary>
    [Fact]
    public void The_wheel_over_an_empty_child_scrolls_the_page()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var inner = new ScrollViewer
            {
                Height = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Height = 40 }
            };
            var page = new StackPanel();
            page.Children.Add(new Border { Height = 400 });
            page.Children.Add(inner);
            page.Children.Add(new Border { Height = 400 });
            var outer = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var window = new Window { Width = 500, Height = 300, ShowInTaskbar = false, Content = outer };
            try
            {
                window.Show();
                window.UpdateLayout();
                outer.ScrollToVerticalOffset(200);
                outer.UpdateLayout();
                var before = outer.VerticalOffset;

                RaiseWheel(inner, -240);
                window.UpdateLayout();

                Assert.True(outer.VerticalOffset > before,
                    "An empty inner region must not trap the wheel.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A child that can still scroll keeps the wheel, and the page does not move with it.</summary>
    [Fact]
    public void A_scrollable_child_keeps_the_wheel_and_nothing_double_scrolls()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var inner = new ScrollViewer
            {
                Height = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Height = 900 }
            };
            var page = new StackPanel();
            page.Children.Add(new Border { Height = 400 });
            page.Children.Add(inner);
            page.Children.Add(new Border { Height = 400 });
            var outer = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var window = new Window { Width = 500, Height = 300, ShowInTaskbar = false, Content = outer };
            try
            {
                window.Show();
                window.UpdateLayout();
                outer.ScrollToVerticalOffset(200);
                outer.UpdateLayout();
                var pageBefore = outer.VerticalOffset;

                RaiseWheel(inner, -240);
                window.UpdateLayout();

                Assert.True(inner.VerticalOffset > 0, "The child must still scroll under the pointer.");
                Assert.Equal(pageBefore, outer.VerticalOffset, 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>With no ancestor able to scroll, the child keeps its own event - as Console needs.</summary>
    [Fact]
    public void Nothing_is_rerouted_when_no_ancestor_can_scroll()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var inner = new ScrollViewer
            {
                Height = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Height = 40 }
            };
            var outer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = inner
            };
            var window = new Window { Width = 500, Height = 300, ShowInTaskbar = false, Content = outer };
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.Null(AppScroll.FindScrollableAncestor(inner, -240));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The behaviour is registered once, centrally, rather than page by page.</summary>
    [Fact]
    public void The_wheel_behaviour_is_registered_by_the_shared_theme()
    {
        var theme = File.ReadAllText(Path.Combine(AppDirectory, "DesignSystem", "AppTheme.cs"));
        Assert.Contains("AppScroll.EnableBoundaryAwareWheel()", theme, StringComparison.Ordinal);

        var offenders = DesignSystemFiles.AllCSharp()
            .Where(path => !path.EndsWith("AppScroll.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("MouseWheelEventArgs", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Empty(offenders);
    }

    // ═════════════════════════════════════════════════════════ 6. access and bans

    /// <summary>A whitelist change is the server's answer, not the switch's optimism.</summary>
    [Fact]
    public async Task A_whitelist_change_waits_for_the_agent_and_re_reads()
    {
        var confirmation = new TaskCompletionSource<OperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AccessFakeClient
        {
            Access = AccessSnapshot(running: true, new UnifiedPlayerAccess { Name = "Xustar" }),
            ModerationResponse = confirmation
        };
        var model = await ReadyModelAsync(client);
        var row = Assert.Single(model.PlayerRows);
        var readsBefore = client.AccessReads;

        row.Whitelisted = true;

        Assert.Equal(PlayerModerationAction.AddToWhitelist, client.LastModeration!.Action);
        Assert.True(row.IsPending);
        Assert.Equal(readsBefore, client.AccessReads);

        client.Access = AccessSnapshot(running: true,
            new UnifiedPlayerAccess { Name = "Xustar", Whitelisted = true });
        confirmation.SetResult(OperationResult.Ok("Confirmed by the server."));
        await WaitUntilAsync(
            () => client.AccessReads > readsBefore && !row.IsPending,
            "The confirmed moderation change did not trigger an authoritative access refresh.");

        Assert.True(client.AccessReads > readsBefore);
        Assert.True(row.Whitelisted);
        Assert.False(row.IsPending);
    }

    /// <summary>A refused operator change puts the switch back where the server has it.</summary>
    [Fact]
    public async Task A_refused_operator_change_reverts_the_switch()
    {
        var client = new AccessFakeClient
        {
            Access = AccessSnapshot(running: true, new UnifiedPlayerAccess { Name = "Xustar" }),
            ModerationFailure = "The server refused."
        };
        var model = await ReadyModelAsync(client);
        var row = Assert.Single(model.PlayerRows);

        row.Operator = true;
        await Settle();

        Assert.False(row.Operator);
        Assert.True(row.HasError);
    }

    /// <summary>The Banned view lists only players the ban files record, with their own detail.</summary>
    [Fact]
    public async Task The_banned_view_shows_only_banned_players_and_only_recorded_detail()
    {
        var created = new DateTimeOffset(2026, 7, 30, 21, 11, 0, TimeSpan.Zero);
        var client = new AccessFakeClient
        {
            Access = AccessSnapshot(running: true,
                new UnifiedPlayerAccess { Name = "Xustar", Whitelisted = true },
                new UnifiedPlayerAccess
                {
                    Name = "Griefer",
                    PlayerBanned = true,
                    BanReason = "Broke spawn",
                    BanSource = "Server",
                    BanCreatedAt = created
                })
        };
        var model = await ReadyModelAsync(client);

        var banned = Assert.Single(model.BannedRows);
        Assert.Equal("Griefer", banned.Name);
        Assert.True(model.HasBannedRows);
        Assert.Equal("1 banned", model.BannedCountText);
        Assert.Equal("Broke spawn", banned.BanReasonText);
        Assert.Equal("Banned by Server", banned.BanSourceText);
        Assert.Contains("7/30/2026", banned.BanCreatedText, StringComparison.Ordinal);
        // No expiry recorded means the file said "forever", which is permanent - not unknown.
        Assert.Equal("Permanent", banned.BanExpiryText);
        Assert.DoesNotContain(model.BannedRows, row => row.Name == "Xustar");
    }

    /// <summary>Nothing is invented when the ban file records nothing.</summary>
    [Fact]
    public async Task Missing_ban_detail_is_named_as_missing()
    {
        var client = new AccessFakeClient
        {
            Access = AccessSnapshot(running: true,
                new UnifiedPlayerAccess { Name = "Griefer", PlayerBanned = true })
        };
        var model = await ReadyModelAsync(client);
        var banned = Assert.Single(model.BannedRows);

        Assert.Equal("No reason recorded", banned.BanReasonText);
        Assert.Equal("Source not recorded", banned.BanSourceText);
        Assert.Equal("Date not recorded", banned.BanCreatedText);
    }

    /// <summary>A pardon removes the row only after the Agent has confirmed it.</summary>
    [Fact]
    public async Task A_pardon_removes_the_row_only_once_the_agent_confirms()
    {
        var client = new AccessFakeClient
        {
            Access = AccessSnapshot(running: true,
                new UnifiedPlayerAccess { Name = "Griefer", PlayerBanned = true })
        };
        var model = await ReadyModelAsync(client);
        var banned = Assert.Single(model.BannedRows);
        Assert.True(banned.CanPardon);

        client.Access = AccessSnapshot(running: true, new UnifiedPlayerAccess { Name = "Griefer" });
        await banned.PardonCommand.ExecuteAsync(null);
        await Settle();

        Assert.Equal(PlayerModerationAction.Pardon, client.LastModeration!.Action);
        Assert.Empty(model.BannedRows);
        Assert.False(model.HasBannedRows);
    }

    /// <summary>A refused pardon keeps the row and shows the server's own words.</summary>
    [Fact]
    public async Task A_refused_pardon_keeps_the_row_and_reports_why()
    {
        var client = new AccessFakeClient
        {
            Access = AccessSnapshot(running: true,
                new UnifiedPlayerAccess { Name = "Griefer", PlayerBanned = true }),
            ModerationFailure = "That player is not banned."
        };
        var model = await ReadyModelAsync(client);
        var banned = Assert.Single(model.BannedRows);

        await banned.PardonCommand.ExecuteAsync(null);
        await Settle();

        Assert.Single(model.BannedRows);
        Assert.True(banned.HasError);
        Assert.Contains("not banned", banned.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The two views are one page, and they are not tabs.</summary>
    [Fact]
    public void Banned_is_a_view_of_access_rather_than_another_destination()
    {
        var access = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerAccessPage.xaml"));

        Assert.Contains("ShowsBannedList", access, StringComparison.Ordinal);
        // The polish pass moved this from a segmented capsule to the app's shared tab language.
        Assert.Contains("AppWorkspaceTabButton", access, StringComparison.Ordinal);
        Assert.DoesNotContain("TabControl", access, StringComparison.Ordinal);
        // The separate operators-and-bans pad stays gone.
        Assert.DoesNotContain("Operators &amp; bans", access, StringComparison.Ordinal);
        // Still six server destinations: Banned did not become a seventh.
        Assert.Equal(6, ServerDestination.All.Count);
    }

    /// <summary>Choosing one view turns the other off.</summary>
    [Fact]
    public async Task The_two_access_views_are_mutually_exclusive()
    {
        var client = new AccessFakeClient { Access = AccessSnapshot(running: true) };
        var model = await ReadyModelAsync(client);

        Assert.True(model.ShowsPlayersList);
        Assert.False(model.ShowsBannedList);

        model.ShowsBannedList = true;
        Assert.False(model.ShowsPlayersList);

        model.ShowsPlayersList = true;
        Assert.False(model.ShowsBannedList);
    }

    [Fact]
    public async Task Overview_players_are_searchable_and_batch_moderation_targets_only_selected_online_rows()
    {
        var client = new AccessFakeClient
        {
            Access = AccessSnapshot(running: true,
                new UnifiedPlayerAccess { Name = "Alpha", Online = true },
                new UnifiedPlayerAccess { Name = "Bravo", Online = true },
                new UnifiedPlayerAccess { Name = "Charlie", Online = false })
        };
        var model = await ReadyModelAsync(client);

        Assert.Equal(2, model.OnlinePlayerRows.Count);
        model.OnlinePlayerSearchText = "brav";
        Assert.Equal("Bravo", Assert.Single(model.FilteredOnlinePlayerRows).Name);

        model.OnlinePlayerSearchText = "";
        foreach (var row in model.OnlinePlayerRows)
            row.IsSelected = true;
        await model.GrantOperatorToSelectedCommand.ExecuteAsync(null);

        Assert.Equal(2, client.Moderations.Count(request => request.Action == PlayerModerationAction.GrantOperator));
        Assert.DoesNotContain(client.Moderations, request => request.PlayerName == "Charlie");
        Assert.False(model.HasSelectedOnlinePlayers);
    }

    /// <summary>A moderation command typed into the Console refreshes what Access shows.</summary>
    [Theory]
    [InlineData("ban Griefer")]
    [InlineData("pardon Griefer")]
    [InlineData("whitelist add Xustar")]
    [InlineData("op Xustar")]
    public void Console_moderation_commands_are_the_ones_that_trigger_a_re_read(string command)
    {
        Assert.True(PlayerModerationPolicy.AffectsPlayerAccess(command));
        Assert.False(PlayerModerationPolicy.AffectsPlayerAccess("say hello"));
    }

    // ═════════════════════════════════════════════════════════ 7. gamerule probes

    /// <summary>
    /// ChunkPilot offers no rule it cannot name for that version, so it sends no probe.
    /// </summary>
    /// <remarks>
    /// Every rule name recorded in the policy is dated against the 1.x release line. On 26.x the
    /// answer is known without asking the server, which is what stops sixteen refused commands
    /// appearing in the user's Console.
    /// </remarks>
    [Theory]
    [InlineData("26.2")]
    [InlineData("26.0")]
    [InlineData("27.1")]
    public void No_rules_are_claimed_for_a_version_outside_the_recorded_release_line(string version)
    {
        Assert.False(GamerulePolicy.CarriesRuleNamesFor(version));
        Assert.Empty(GamerulePolicy.Supported(version));
    }

    /// <summary>The versions the table was written for still work.</summary>
    [Theory]
    [InlineData("1.21.1")]
    [InlineData("1.20.4")]
    [InlineData("1.17")]
    public void Rules_are_still_offered_for_the_versions_they_were_recorded_against(string version)
    {
        Assert.True(GamerulePolicy.CarriesRuleNamesFor(version));
        Assert.NotEmpty(GamerulePolicy.Supported(version));
    }

    /// <summary>Opening Settings sends no speculative gamerule command.</summary>
    [Fact]
    public async Task Opening_settings_does_not_probe_the_server_for_game_rules()
    {
        var client = new AccessFakeClient
        {
            Access = AccessSnapshot(running: true),
            Gamerules = new GameruleStateResponse
            {
                ServerId = ServerId,
                ServerRunning = true,
                CanChange = false,
                UnavailableReason = "Game rules are not available for this server's Minecraft version.",
                Rules = []
            }
        };
        var model = await ReadyModelAsync(client);

        model.NavigateServerDestinationCommand.Execute(ServerDestination.Settings);
        await Settle();
        await model.RefreshGamerulesCommand.ExecuteAsync(null);

        Assert.Empty(client.SentCommands);
        Assert.True(model.ShowsGameruleUnavailable);
        Assert.False(model.ShowsGamerules);
    }

    /// <summary>One concise sentence, no dead controls, and no failed-probe history.</summary>
    [Fact]
    public async Task The_unavailable_state_is_one_sentence_about_the_version()
    {
        var client = new AccessFakeClient
        {
            Access = AccessSnapshot(running: true),
            Gamerules = new GameruleStateResponse
            {
                ServerId = ServerId,
                ServerRunning = true,
                CanChange = false,
                UnavailableReason = "Game rules are not available for this server's Minecraft version.",
                Rules = []
            }
        };
        var model = await ReadyModelAsync(client);
        model.NavigateServerDestinationCommand.Execute(ServerDestination.Settings);
        await Settle();

        Assert.Equal("Game rules are not available for this server's Minecraft version.",
            model.GameruleUnavailableReason);
        Assert.Empty(model.Gamerules);
        Assert.DoesNotContain("<--[HERE]", model.GameruleUnavailableReason, StringComparison.Ordinal);
        Assert.DoesNotContain("Incorrect argument", model.GameruleUnavailableReason, StringComparison.Ordinal);
    }

    /// <summary>CP-2026-001 is still open, and the register still carries its evidence.</summary>
    [Fact]
    public void The_open_game_rule_defect_is_still_recorded_as_open()
    {
        var register = File.ReadAllText(
            Path.Combine(DesignSystemFiles.RepositoryRoot, "docs", "BUG-REGISTER.md"));

        Assert.Contains("CP-2026-001", register, StringComparison.Ordinal);
        Assert.Contains("**Open**", register, StringComparison.Ordinal);
        Assert.Contains("Incorrect argument for command", register, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════ 8. navigation selection

    /// <summary>Reaching Servers from the Dashboard selects Servers in the rail.</summary>
    [Fact]
    public void A_dashboard_deep_link_selects_the_destination_it_opens()
    {
        var navigation = new NavigationService();
        Assert.Equal(GlobalDestination.Dashboard, navigation.SelectedGlobalItem?.Page);

        navigation.NavigateGlobal(GlobalDestination.Servers);

        Assert.Equal(GlobalDestination.Servers, navigation.CurrentGlobalPage);
        Assert.Equal(GlobalDestination.Servers, navigation.SelectedGlobalItem?.Page);
    }

    /// <summary>The same route, through the view model's own navigation command.</summary>
    [Fact]
    public async Task Open_servers_moves_the_selection_off_dashboard()
    {
        var client = new AccessFakeClient { Access = AccessSnapshot(running: false) };
        var model = new MainViewModel(client, new SilentDialogs());
        await model.InitializeAsync();

        model.NavigateCommand.Execute(GlobalDestination.Dashboard);
        Assert.Equal(GlobalDestination.Dashboard, model.Navigation.SelectedGlobalItem?.Page);

        model.NavigateCommand.Execute(GlobalDestination.Servers);

        Assert.Equal(GlobalDestination.Servers, model.CurrentPage);
        Assert.Equal(GlobalDestination.Servers, model.Navigation.SelectedGlobalItem?.Page);

        // Repeating the same navigation is idempotent.
        model.NavigateCommand.Execute(GlobalDestination.Servers);
        Assert.Equal(GlobalDestination.Servers, model.Navigation.SelectedGlobalItem?.Page);
    }

    /// <summary>A server workspace clears the global selection, and leaving it restores one.</summary>
    [Fact]
    public void A_server_workspace_carries_no_global_selection()
    {
        var navigation = new NavigationService();
        navigation.NavigateGlobal(GlobalDestination.Servers);
        navigation.OpenServer(ServerId);

        Assert.Null(navigation.SelectedGlobalItem);

        navigation.NavigateGlobal(GlobalDestination.Activity);
        Assert.Equal(GlobalDestination.Activity, navigation.SelectedGlobalItem?.Page);
    }

    /// <summary>The shell mirrors the navigation service instead of recomputing the answer.</summary>
    [Fact]
    public void The_shell_reads_the_selection_from_navigation_state()
    {
        var shell = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.xaml.cs"));

        Assert.Contains("viewModel.Navigation.SelectedGlobalItem", shell, StringComparison.Ordinal);
        Assert.Contains("nameof(NavigationService.CurrentGlobalPage)", shell, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════ helpers

    private static CheckBox NewSwitch(string? content) => new()
    {
        Style = (Style)Application.Current.FindResource("AppToggleSwitch"),
        Content = content
    };

    private static ScrollViewer Scroller(double contentHeight, double viewportHeight)
    {
        return new ScrollViewer
        {
            Height = viewportHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Height = contentHeight }
        };
    }

    private static void RaiseWheel(UIElement target, int delta) =>
        target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = target
        });

    private static string[] EyebrowSections(string xamlFile) =>
        XDocument.Load(xamlFile).Root!
            .DescendantsAndSelf()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Where(element => element.Attribute("Style")?.Value.Contains("AppEyebrowText", StringComparison.Ordinal) == true)
            .Select(element => element.Attribute("Text")?.Value ?? "")
            .ToArray();

    private static T? FindByName<T>(Control scope, string name) where T : FrameworkElement
    {
        scope.ApplyTemplate();
        return scope.Template?.FindName(name, scope) as T;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static PlayerAccessSnapshot AccessSnapshot(bool running, params UnifiedPlayerAccess[] players) => new()
    {
        ServerId = ServerId,
        ServerRunning = running,
        MaxPlayers = 10,
        Stamp = Guid.NewGuid().ToString("N"),
        Players = players
    };

    private static async Task<MainViewModel> ReadyModelAsync(IAgentClient client)
    {
        var model = new MainViewModel(client, new SilentDialogs());
        await model.InitializeAsync();
        model.SelectedServer = model.Servers.Single();
        await Settle();
        return model;
    }

    private static async Task Settle()
    {
        for (var index = 0; index < 8; index++)
            await Task.Yield();
        await Task.Delay(30);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), failureMessage);
    }

    /// <summary>A disposable server folder with a real server.properties in it.</summary>
    private sealed class ServerFixture : IDisposable
    {
        public ServerFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "ChunkPilot-UxCorrections-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            File.WriteAllText(Path.Combine(RootPath, "server.properties"), Content);
        }

        public const string Content =
            "#Minecraft server properties\r\n" +
            "motd=A Minecraft Server\r\nserver-port=25565\r\nmax-players=10\r\ndifficulty=normal\r\n" +
            "gamemode=survival\r\nview-distance=10\r\nsimulation-distance=10\r\nallow-flight=false\r\n" +
            "pvp=true\r\nonline-mode=true\r\nwhite-list=false\r\nspawn-protection=16\r\n" +
            "enable-command-block=false\r\nhardcore=false\r\nforce-gamemode=false\r\nplayer-idle-timeout=0\r\n";

        public string RootPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
                // A disposable fixture that cannot be removed is not a test failure.
            }
        }
    }

    /// <summary>An Agent that answers the workspace reads and records every command it is sent.</summary>
    private class AccessFakeClient : IAgentClient
    {
        public PlayerAccessSnapshot Access { get; set; } = new();
        public GameruleStateResponse Gamerules { get; set; } = new();
        public string? ModerationFailure { get; set; }
        public TaskCompletionSource<OperationResult>? ModerationResponse { get; set; }
        public string ServerRootPath { get; set; } = @"C:\fixture";
        public string MinecraftVersion { get; set; } = "26.2";

        public int AccessReads { get; private set; }
        public PlayerModerationRequest? LastModeration { get; private set; }
        public List<PlayerModerationRequest> Moderations { get; } = [];
        public List<string> SentCommands { get; } = [];

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            switch (operation)
            {
                case "GetPlayerAccess":
                    AccessReads++;
                    return Task.FromResult((TResponse)(object)Access);
                case "ReadGamerules":
                    return Task.FromResult((TResponse)(object)Gamerules);
                case "ModeratePlayer":
                    LastModeration = (PlayerModerationRequest)payload!;
                    Moderations.Add(LastModeration);
                    if (ModerationResponse is not null)
                        return AwaitModerationAsync<TResponse>(ModerationResponse.Task, cancellationToken);
                    return Task.FromResult((TResponse)(object)(ModerationFailure is null
                        ? OperationResult.Ok("Confirmed by the server.")
                        : OperationResult.Fail(ModerationFailure)));
                case "SendCommand":
                    SentCommands.Add(payload?.ToString() ?? "");
                    return Task.FromResult((TResponse)(object)OperationResult.Ok("sent"));
            }
            return Task.FromResult((TResponse)Default(operation));
        }

        private static async Task<TResponse> AwaitModerationAsync<TResponse>(
            Task<OperationResult> response,
            CancellationToken cancellationToken)
        {
            var result = await response.WaitAsync(cancellationToken).ConfigureAwait(false);
            return (TResponse)(object)result;
        }

        protected object Default(string operation) => operation switch
        {
            "Dashboard" => new DashboardSnapshot
            {
                AgentConnected = true,
                Host = new HostSnapshot { LanAddress = "10.0.0.140", TotalMemoryBytes = 32L * 1024 * 1024 * 1024 },
                Servers =
                [
                    new ServerSnapshot
                    {
                        Definition = new ServerDefinition
                        {
                            Id = ServerId,
                            Name = "test survival",
                            RootPath = ServerRootPath,
                            Executable = Path.Combine(ServerRootPath, "java.exe"),
                            WorkingDirectory = ServerRootPath,
                            Port = 25565,
                            MinimumRamMb = 1_024,
                            MaximumRamMb = 4_096,
                            MinecraftVersion = MinecraftVersion
                        },
                        State = Access.ServerRunning ? ServerState.Running : ServerState.Stopped,
                        PlayerAccessStamp = Access.Stamp
                    }
                ]
            },
            "GetCapabilities" => new ServerCapabilityProfile { SupportsGamerules = false },
            "GetNetworkConfiguration" => new NetworkConfiguration(),
            "ListBackups" => Array.Empty<BackupRecord>(),
            "ListSchedules" => Array.Empty<ScheduleEntry>(),
            "ListFiles" => Array.Empty<FileSystemEntry>(),
            "Inventory" => Array.Empty<ModPluginEntry>(),
            "Diagnostics" => Array.Empty<DiagnosticFinding>(),
            "ListWorlds" => Array.Empty<WorldEntry>(),
            "ListAutomationRecipes" => Array.Empty<AutomationRecipe>(),
            "AutomationRecipeTemplates" => Array.Empty<AutomationRecipe>(),
            "GetCrossplayConfiguration" => new CrossplayConfiguration(),
            "ListDatapacks" => Array.Empty<DatapackInventoryItem>(),
            "GetResourcePackConfiguration" => new ResourcePackConfiguration(),
            "GetSetting" => new TextResponse(""),
            "GetUpdateSource" => new UpdateSourceResponse(null),
            "GetUpdatePreferences" => new UpdatePreferences(),
            "ListVersions" => Array.Empty<VersionSnapshot>(),
            "ListUpdateHistory" => Array.Empty<UpdateHistoryEntry>(),
            "RegisterUiSession" => new UiSessionRegistrationResult(new ApplicationSession(), false, ""),
            _ => OperationResult.Ok("ok")
        };
    }

    /// <summary>The same Agent, plus a real server.properties it reads and writes.</summary>
    private sealed class ConfigurationFakeClient : AccessFakeClient
    {
        private readonly string root;

        public ConfigurationFakeClient(string rootPath)
        {
            root = rootPath;
            ServerRootPath = rootPath;
            Access = new PlayerAccessSnapshot { ServerId = ServerId, ServerRunning = true, Stamp = "fixture" };
        }

        public string? WriteFailure { get; set; }
        public Dictionary<string, string>? LastWrite { get; private set; }

        public override Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            switch (operation)
            {
                case "GetServerProperties":
                {
                    var document = ServerPropertiesDocument.Parse(
                        File.ReadAllText(Path.Combine(root, "server.properties")));
                    return Task.FromResult((TResponse)(object)new ServerPropertiesResponse(
                        new Dictionary<string, string>(document.Values, StringComparer.OrdinalIgnoreCase),
                        ""));
                }
                case "UpdateServerProperties":
                {
                    var request = (ServerPropertiesRequest)payload!;
                    if (WriteFailure is not null)
                        return Task.FromResult((TResponse)(object)OperationResult.Fail(WriteFailure));
                    LastWrite = new Dictionary<string, string>(request.Values, StringComparer.OrdinalIgnoreCase);
                    var path = Path.Combine(root, "server.properties");
                    var document = ServerPropertiesDocument.Parse(File.ReadAllText(path));
                    foreach (var pair in request.Values)
                        document.Set(pair.Key, pair.Value);
                    File.WriteAllText(path, document.ToString());
                    return Task.FromResult((TResponse)(object)OperationResult.Ok("server.properties updated."));
                }
            }
            return base.SendAsync<TResponse>(operation, payload, cancellationToken);
        }
    }

    private sealed class SilentDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => true;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
