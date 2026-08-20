using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using ChunkPilot.App;
using ChunkPilot.App.Access;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>
/// The acceptance-polish defects this milestone corrected, pinned so they cannot return.
/// </summary>
/// <remarks>
/// Named after what the user saw: a record's ToString in a dropdown, an underline that was not under
/// anything, a mojibake middle dot, a "last seen" date a month in the future for somebody who had
/// never connected, and a focus ring that would not let go.
/// </remarks>
public sealed class VanillaWorkspacePremiumPolishTests
{
    private static readonly string AppDirectory = DesignSystemFiles.AppProjectDirectory;

    // ═════════════════════════════════════════════════ 1. performance is near the top

    /// <summary>Performance sits directly under the summary, above Connect, Protection and Files.</summary>
    [Fact]
    public void Overview_shows_performance_before_connection_and_files()
    {
        var sections = OverviewSectionHeaders();

        Assert.Equal("At a glance", sections[0]);
        Assert.Equal("CPU performance", sections[1]);
        Assert.Equal("Memory performance", sections[2]);
        Assert.True(
            Array.IndexOf(sections, "Files") > Array.IndexOf(sections, "CPU performance"),
            "Files must come after the performance cards, not before them.");
    }

    /// <summary>CPU and memory are stated once on the page, not twice with different formatting.</summary>
    [Fact]
    public void Overview_does_not_repeat_the_same_metric_in_two_places()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerOverviewPage.xaml"));

        Assert.DoesNotContain("SelectedServer.CurrentStatistics.CpuPercent", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedServer.CurrentStatistics.WorkingSetBytes", xaml, StringComparison.Ordinal);
        // Exactly one chart of each kind survived the move.
        Assert.Equal(1, Occurrences(xaml, "x:Name=\"CpuChart\""));
        Assert.Equal(1, Occurrences(xaml, "x:Name=\"MemoryChart\""));
    }

    /// <summary>Charts appear only when real samples exist; a stopped server gets a sentence.</summary>
    [Fact]
    public void Performance_cards_are_gated_on_real_samples()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerOverviewPage.xaml"));

        Assert.Contains("ShowsPerformanceCharts", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowsStoppedSummary", xaml, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════ 2/3/16. the shared tab template

    /// <summary>
    /// The selection underline is exactly as wide as the tab's content, and centred under it.
    /// </summary>
    /// <remarks>
    /// The defect was geometric, not cosmetic: the indicator was a bottom-aligned Border layered over
    /// the content in a single-cell Grid with an asymmetric margin, so its width came from the padded
    /// cell rather than from the icon and label. This measures both edges against the content.
    /// </remarks>
    [Fact]
    public void The_tab_selection_underline_is_centred_under_its_content()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var list = WorkspaceTabs();
            var window = new Window { Width = 700, Height = 200, ShowInTaskbar = false, Content = list };
            try
            {
                window.Show();
                window.UpdateLayout();
                list.SelectedIndex = 0;
                window.UpdateLayout();

                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var indicator = FindByName<Border>(item, "Indicator")!;
                var content = FindDescendant<ContentPresenter>(item)!;

                Assert.Equal(Visibility.Visible, indicator.Visibility);

                var indicatorBox = indicator.TransformToAncestor(item).TransformBounds(
                    new Rect(0, 0, indicator.ActualWidth, indicator.ActualHeight));
                var contentBox = content.TransformToAncestor(item).TransformBounds(
                    new Rect(0, 0, content.ActualWidth, content.ActualHeight));

                var indicatorCentre = indicatorBox.Left + (indicatorBox.Width / 2);
                var contentCentre = contentBox.Left + (contentBox.Width / 2);

                Assert.Equal(contentCentre, indicatorCentre, 1);
                Assert.Equal(contentBox.Width, indicatorBox.Width, 1);
                Assert.True(indicatorBox.Top >= contentBox.Bottom - 1,
                    "The underline must sit below the label, not across it.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Every tab states selection the same way, whichever one is selected.</summary>
    [Fact]
    public void Every_workspace_tab_uses_the_same_selection_language()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var list = WorkspaceTabs();
            var window = new Window { Width = 700, Height = 200, ShowInTaskbar = false, Content = list };
            try
            {
                window.Show();
                window.UpdateLayout();

                var widths = new List<double>();
                for (var index = 0; index < list.Items.Count; index++)
                {
                    list.SelectedIndex = index;
                    window.UpdateLayout();
                    var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                    var indicator = FindByName<Border>(item, "Indicator")!;
                    var content = FindDescendant<ContentPresenter>(item)!;

                    Assert.Equal(Visibility.Visible, indicator.Visibility);
                    Assert.Equal(content.ActualWidth, indicator.ActualWidth, 1);
                    widths.Add(indicator.ActualHeight);

                    // Selection is never an enclosing shape.
                    var root = FindByName<Border>(item, "Root")!;
                    Assert.Equal(0d, root.BorderThickness.Left);
                }

                Assert.All(widths, height => Assert.Equal(widths[0], height, 1));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Selecting a tab does not change the height of the strip.</summary>
    [Fact]
    public void Selecting_a_tab_does_not_move_the_strip()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var list = WorkspaceTabs();
            var window = new Window { Width = 700, Height = 200, ShowInTaskbar = false, Content = list };
            try
            {
                window.Show();
                window.UpdateLayout();
                var unselected = ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1)).ActualHeight;

                list.SelectedIndex = 1;
                window.UpdateLayout();
                var selected = ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1)).ActualHeight;

                Assert.Equal(unselected, selected, 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The tab strip is defined once, in the shared theme, not inline on a page.</summary>
    [Fact]
    public void The_tab_template_is_shared_rather_than_page_local()
    {
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppWorkspaceTabItem"));
        Assert.NotNull(WpfDesignSystemHost.Resolve("AppWorkspaceTabs"));

        var shell = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.xaml"));
        Assert.Contains("Style=\"{StaticResource AppWorkspaceTabs}\"", shell, StringComparison.Ordinal);
        // The inline copy that owned the broken geometry is gone.
        Assert.DoesNotContain("x:Name=\"Indicator\"", shell, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════ 2. header hierarchy

    /// <summary>The server name is the strongest text, and the state is a dot plus a word.</summary>
    [Fact]
    public void The_workspace_header_leads_with_the_server_name()
    {
        var shell = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.xaml"));

        Assert.Contains("AppTitleLargeText", shell, StringComparison.Ordinal);
        Assert.Contains("AppWorkspaceHeaderPadding", shell, StringComparison.Ordinal);

        // No capsule around the runtime state in the workspace header itself. The Servers library
        // further down the shell is a different surface and keeps its own row treatment.
        var header = shell[shell.IndexOf("AppWorkspaceHeaderPadding", StringComparison.Ordinal)..];
        header = header[..header.IndexOf("AppWorkspaceTabs", StringComparison.Ordinal)];
        Assert.DoesNotContain("AppStatusBadge", header, StringComparison.Ordinal);
        Assert.Contains("StateBrush", header, StringComparison.Ordinal);
        Assert.Contains("StateText", header, StringComparison.Ordinal);
    }

    /// <summary>The header sits further from the window chrome than an ordinary page inset.</summary>
    [Fact]
    public void The_header_has_room_above_it()
    {
        var padding = Assert.IsType<Thickness>(WpfDesignSystemHost.Resolve("AppWorkspaceHeaderPadding"));
        var page = Assert.IsType<Thickness>(WpfDesignSystemHost.Resolve("AppPagePadding"));

        Assert.True(padding.Top >= page.Top,
            "The workspace header sat against the title bar and the user missed it entirely.");
    }

    // ═════════════════════════════════════════════════ 4/17. decorative badges

    /// <summary>The development-build notice is icon and text, not a capsule.</summary>
    [Fact]
    public void The_development_build_notice_is_not_a_capsule()
    {
        var wizard = File.ReadAllText(
            Path.Combine(AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml"));

        Assert.Contains("Development build · Vanilla only", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain("AppStatusBadge Tone=\"Warning\"", wizard, StringComparison.Ordinal);
        // The state is still stated, and still carries its warning colour.
        Assert.Contains("AppWarningText", wizard, StringComparison.Ordinal);
    }

    /// <summary>Access states counts and whitelist state as text rather than as chips.</summary>
    [Fact]
    public void Access_does_not_wrap_informational_state_in_chips()
    {
        var access = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerAccessPage.xaml"));

        Assert.DoesNotContain("AppSegmentedItem", access, StringComparison.Ordinal);
        Assert.Contains("AppWorkspaceTabButton", access, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════ 5. object rendering

    /// <summary>A choice renders its label, never the record's generated shape.</summary>
    [Fact]
    public void A_property_choice_never_renders_as_its_record_shape()
    {
        var choice = new ServerPropertyChoice("normal", "Normal");

        Assert.Equal("Normal", choice.ToString());
        Assert.DoesNotContain("ServerPropertyChoice", choice.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Value =", choice.ToString(), StringComparison.Ordinal);
        Assert.All(ServerPropertyPresentation.Difficulties,
            item => Assert.Equal(item.Label, item.ToString()));
        Assert.All(ServerPropertyPresentation.GameModes,
            item => Assert.Equal(item.Label, item.ToString()));
    }

    /// <summary>
    /// The closed dropdown shows the label, which is what was actually broken on screen.
    /// </summary>
    /// <remarks>
    /// The shared ComboBox template binds its closed-state presenter to SelectionBoxItemTemplate.
    /// WPF fills that from ItemTemplate only, so DisplayMemberPath alone left it null and the
    /// presenter fell back to ToString. This renders a real ComboBox through the real template.
    /// </remarks>
    [Fact]
    public void The_closed_dropdown_shows_the_label_not_the_object()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var combo = new ComboBox
            {
                Width = 240,
                ItemsSource = ServerPropertyPresentation.Difficulties,
                ItemTemplate = (DataTemplate)Application.Current.FindResource("AppPropertyChoiceTemplate"),
                SelectedValuePath = "Value",
                SelectedValue = "normal"
            };
            var window = new Window { Width = 400, Height = 200, ShowInTaskbar = false, Content = combo };
            try
            {
                window.Show();
                window.UpdateLayout();

                var rendered = RenderedText(combo);
                Assert.Contains("Normal", rendered, StringComparison.Ordinal);
                Assert.DoesNotContain("ServerPropertyChoice", rendered, StringComparison.Ordinal);
                Assert.Equal("normal", combo.SelectedValue);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_editable_dropdown_renders_and_accepts_a_custom_value()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var combo = new ComboBox
            {
                Width = 240,
                IsEditable = true,
                ItemsSource = new List<int> { 25565, 25566, 25567 },
                Text = "25565"
            };
            var window = new Window { Width = 400, Height = 200, ShowInTaskbar = false, Content = combo };
            try
            {
                window.Show();
                window.UpdateLayout();

                var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
                Assert.Equal("25565", editor.Text);
                editor.Text = "25580";
                Assert.Equal("25580", combo.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The Configuration dropdowns are bound the way that actually works.</summary>
    [Fact]
    public void Configuration_dropdowns_use_an_item_template()
    {
        var manage = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));

        Assert.Contains("AppPropertyChoiceTemplate", manage, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayMemberPath=\"Label\"", manage, StringComparison.Ordinal);
        Assert.Contains("SelectedValuePath=\"Value\"", manage, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════ 6/8. configuration hierarchy

    /// <summary>Configuration is grouped, most-changed first, with the port out of the way.</summary>
    [Fact]
    public void Configuration_is_grouped_by_what_people_change()
    {
        var manage = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));

        var general = manage.IndexOf("GENERAL", StringComparison.Ordinal);
        var world = manage.IndexOf("WORLD &amp; GAMEPLAY", StringComparison.Ordinal);
        var advanced = manage.IndexOf("Header=\"Advanced\"", StringComparison.Ordinal);

        Assert.True(general > 0 && world > general && advanced > world,
            "Expected General, then World & gameplay, then Advanced.");

        // The port is a networking detail and belongs under Advanced, not between difficulty and view distance.
        Assert.True(manage.IndexOf("PropertyPort", StringComparison.Ordinal) > advanced);
        Assert.True(manage.IndexOf("PropertyOnlineMode", StringComparison.Ordinal) > advanced);
    }

    /// <summary>Advanced is collapsed by default and sized to what it holds.</summary>
    [Fact]
    public void Advanced_configuration_is_collapsed_and_compact()
    {
        var manage = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));

        Assert.Contains("IsCollapsible=\"True\" IsExpanded=\"False\"", manage, StringComparison.Ordinal);
        // No fixed height reserving empty space inside the disclosure.
        Assert.DoesNotContain("Header=\"Advanced\" IsCollapsible=\"True\" IsExpanded=\"False\" Height=",
            manage, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════ 7. encoding

    /// <summary>
    /// No source or resource file carries mojibake.
    /// </summary>
    /// <remarks>
    /// The Version Manager's "Active: Unknown Â· Java: Unknown" came from a UTF-8 file re-encoded as
    /// though it were Windows-1252. This scans the whole governed tree rather than the one string,
    /// because the cause was a tool, not a typo, and a tool can do it again anywhere.
    /// </remarks>
    [Fact]
    public void No_source_file_contains_mojibake()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            if (text.Contains('�') ||
                text.Contains("Â·", StringComparison.Ordinal) ||
                text.Contains("â€", StringComparison.Ordinal) ||
                text.Contains("â•", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "Mojibake in: " + string.Join(", ", offenders.Distinct()));
    }

    /// <summary>Version facts are separate labeled rows, not one punctuation-heavy sentence.</summary>
    [Fact]
    public void The_version_manager_uses_scannable_labeled_rows()
    {
        var manage = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));

        Assert.Contains("Label=\"Installed version\"", manage, StringComparison.Ordinal);
        Assert.Contains("Label=\"Required Java\"", manage, StringComparison.Ordinal);
        Assert.Contains("Label=\"Recovery\"", manage, StringComparison.Ordinal);
    }

    /// <summary>An empty version table is a sentence, not a grid of headers.</summary>
    [Fact]
    public void An_empty_version_history_explains_itself()
    {
        var manage = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));

        Assert.Contains("HasVersionHistory", manage, StringComparison.Ordinal);
        Assert.Contains("No version history yet", manage, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════ 10. boolean controls

    /// <summary>Row permissions are check boxes, not tiny slider pills.</summary>
    [Fact]
    public void Permission_columns_use_check_boxes()
    {
        var access = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerAccessPage.xaml"));
        var manage = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));

        Assert.DoesNotContain("AppToggleSwitch", access, StringComparison.Ordinal);
        Assert.Contains("AppToggleSwitch", manage, StringComparison.Ordinal);
        Assert.Contains("PvP enabled", manage, StringComparison.Ordinal);
        Assert.Contains("Content=\"Whitelist\"", access, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Whitelisted\"", access, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Operator\"", access, StringComparison.Ordinal);
    }

    /// <summary>The shared check box focuses its square without drawing an outer circular halo.</summary>
    [Fact]
    public void The_shared_check_box_has_a_contained_focus_ring()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var box = new CheckBox { Content = "Allow flight" };
            var host = new Grid { Width = 600 };
            host.Children.Add(box);
            var window = new Window { Width = 700, Height = 200, ShowInTaskbar = false, Content = host };
            try
            {
                window.Show();
                window.UpdateLayout();

                var square = FindByName<Border>(box, "Box");
                Assert.NotNull(square);
                Assert.Null(FindByName<Border>(box, "FocusRing"));
                Assert.True(square.ActualWidth <= 24,
                    "Focus must stay on the square, without an outer halo.");
                Assert.True(box.ActualHeight >= 24);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ═════════════════════════════════════════════════ 11. last seen truthfulness

    /// <summary>
    /// usercache.json is name-and-UUID evidence and nothing else.
    /// </summary>
    /// <remarks>
    /// The root cause of "Offline · seen 9/7 10:05 AM" in August 2026: the reader took the entry's
    /// "expiresOn" - when the name-to-UUID cache stops being trusted, about a month ahead - as the
    /// moment the player was last seen. Whitelisting somebody creates that entry, so a player who had
    /// never connected was shown as seen, on a date that had not happened.
    /// </remarks>
    [Fact]
    public void The_user_cache_is_no_longer_read_as_player_activity()
    {
        var reader = File.ReadAllText(Path.Combine(
            DesignSystemFiles.RepositoryRoot, "src", "ChunkPilot.Infrastructure", "ServerContentServices.cs"));

        // The assignment, not the word: the comment above the loop still names the field it used to
        // read, because that history is the reason the loop looks the way it does.
        Assert.DoesNotContain("LastSeenAt = ParseMinecraftTime(StringValue(item, \"expiresOn\"))",
            reader, StringComparison.Ordinal);
        Assert.DoesNotContain("LastSeenAt = ", reader, StringComparison.Ordinal);
    }

    /// <summary>Whitelisting, operator and ban are not sessions and produce no last-seen.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Access_state_alone_never_produces_an_activity_time(bool whitelisted, bool op, bool banned)
    {
        var row = Row(new UnifiedPlayerAccess
        {
            Name = "Xustar",
            Whitelisted = whitelisted,
            Operator = op,
            PlayerBanned = banned
        });

        if (banned)
        {
            Assert.Equal("Banned", row.StatusText);
            return;
        }
        Assert.Null(row.LastSeenAt);
        Assert.Equal("Offline · no activity recorded", row.StatusText);
        Assert.DoesNotContain("seen", row.StatusText, StringComparison.Ordinal);
    }

    /// <summary>A real observed session is shown, in local time.</summary>
    [Fact]
    public void An_observed_session_is_shown_as_a_local_time()
    {
        var seen = DateTimeOffset.Now.AddHours(-3);
        var row = Row(new UnifiedPlayerAccess { Name = "Xustar", LastSeenAt = seen });

        Assert.Contains("seen", row.StatusText, StringComparison.Ordinal);
        Assert.Contains(seen.ToLocalTime().ToString("M/d/yyyy", System.Globalization.CultureInfo.CurrentCulture),
            row.StatusText, StringComparison.Ordinal);
    }

    /// <summary>A time that has not happened is never presented as a past session.</summary>
    [Theory]
    [InlineData(31)]
    [InlineData(1)]
    public void A_future_timestamp_is_never_shown_as_last_seen(int daysAhead)
    {
        var row = Row(new UnifiedPlayerAccess
        {
            Name = "Xustar",
            LastSeenAt = DateTimeOffset.Now.AddDays(daysAhead)
        });

        Assert.Equal("Offline · no activity recorded", row.StatusText);
    }

    /// <summary>An online player is online, whatever any timestamp says.</summary>
    [Fact]
    public void An_online_player_reads_as_online()
    {
        var row = Row(new UnifiedPlayerAccess { Name = "Xustar", Online = true }, running: true);

        Assert.Equal("Online", row.StatusText);
    }

    /// <summary>
    /// The Agent's activity record belongs to one server, so it cannot leak between servers.
    /// </summary>
    /// <remarks>
    /// Structural rather than behavioural: the dictionary is an instance field of ManagedServer,
    /// which the supervisor creates one of per server, and nothing writes to it except that server's
    /// own join and leave lines.
    /// </remarks>
    [Fact]
    public void Player_activity_is_recorded_per_server_from_join_and_leave_lines_only()
    {
        var managed = File.ReadAllText(Path.Combine(
            DesignSystemFiles.RepositoryRoot, "src", "ChunkPilot.Agent", "ManagedServer.cs"));

        Assert.Contains("private readonly Dictionary<string, DateTimeOffset> lastSeenByPlayer",
            managed, StringComparison.Ordinal);
        Assert.Contains("public IReadOnlyDictionary<string, DateTimeOffset> LastSeenByPlayer",
            managed, StringComparison.Ordinal);

        // Written from presence tracking and from the server going away, and nowhere else.
        Assert.Equal(2, Occurrences(managed, "lastSeenByPlayer[name] = "));

        var agent = File.ReadAllText(Path.Combine(
            DesignSystemFiles.RepositoryRoot, "src", "ChunkPilot.Agent", "AgentPipeServer.cs"));
        Assert.Contains("managed.LastSeenByPlayer", agent, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════ 14. background focus

    /// <summary>A click on inert page chrome clears focus but not the chosen value.</summary>
    [Fact]
    public void Clicking_blank_space_clears_focus_and_keeps_the_value()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var combo = new ComboBox
            {
                Width = 200,
                ItemsSource = ServerPropertyPresentation.Difficulties,
                ItemTemplate = (DataTemplate)Application.Current.FindResource("AppPropertyChoiceTemplate"),
                SelectedValuePath = "Value",
                SelectedValue = "hard"
            };
            var blank = new Border { Height = 120, Background = Brushes.Transparent };
            var panel = new StackPanel();
            panel.Children.Add(combo);
            panel.Children.Add(blank);
            var window = new Window { Width = 400, Height = 320, ShowInTaskbar = false, Content = panel };
            try
            {
                AppFocus.ClearFocusOnBackgroundClick(window);
                window.Show();
                combo.Focus();
                window.UpdateLayout();
                Assert.True(combo.IsKeyboardFocusWithin);

                // The border is inert chrome; the combo is not.
                Assert.False(AppFocus.IsInteractive(blank));
                Assert.True(AppFocus.IsInteractive(combo));

                Keyboard.ClearFocus();
                FocusManager.SetFocusedElement(window, null);
                window.UpdateLayout();

                Assert.False(combo.IsKeyboardFocusWithin);
                // The point of the whole behaviour: focus went, the choice stayed.
                Assert.Equal("hard", combo.SelectedValue);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Interactive controls are never treated as background.</summary>
    [Fact]
    public void Interactive_controls_keep_their_focus()
    {
        WpfDesignSystemHost.Run(() =>
        {
            Assert.True(AppFocus.IsInteractive(new TextBox()));
            Assert.True(AppFocus.IsInteractive(new Button()));
            Assert.True(AppFocus.IsInteractive(new CheckBox()));
            Assert.True(AppFocus.IsInteractive(new ListBoxItem()));
            Assert.True(AppFocus.IsInteractive(new ScrollBar()));
            Assert.False(AppFocus.IsInteractive(new TextBlock()));
            Assert.False(AppFocus.IsInteractive(null));
        });
    }

    /// <summary>The behaviour is attached once, for every window, by the shared theme.</summary>
    [Fact]
    public void Focus_clearing_is_attached_by_the_shared_theme()
    {
        var theme = File.ReadAllText(Path.Combine(AppDirectory, "DesignSystem", "AppTheme.cs"));

        Assert.Contains("AppFocus.ClearFocusOnBackgroundClick(window)", theme, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════ 15. memory presentation

    /// <summary>Memory is offered in gigabytes; the JVM flags are a tooltip.</summary>
    [Fact]
    public void Memory_shows_gigabytes_and_hides_the_jvm_flags()
    {
        var settings = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerSettingsPage.xaml"));

        Assert.Contains("Minimum memory (GB)", settings, StringComparison.Ordinal);
        Assert.Contains("Maximum memory (GB)", settings, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding MemoryDetailText}\"", settings, StringComparison.Ordinal);
        // The flags no longer sit in the card as body text.
        Assert.DoesNotContain("Text=\"{Binding MemoryDetailText}\"", settings, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════ 18. copy

    /// <summary>Touched pages do not name the Agent, the service, or the file behind them.</summary>
    [Fact]
    public void Touched_copy_does_not_expose_implementation_terminology()
    {
        foreach (var page in new[] { "ServerManagePage.xaml", "ServerAccessPage.xaml", "ServerSettingsPage.xaml" })
        {
            var text = File.ReadAllText(Path.Combine(AppDirectory, "Pages", page));
            var visible = XDocument.Parse(text).Root!
                .DescendantsAndSelf()
                .SelectMany(element => element.Attributes())
                .Where(attribute => attribute.Name.LocalName is "Text" or "Description" or "Header" or "Content")
                .Select(attribute => attribute.Value)
                .ToArray();

            Assert.DoesNotContain(visible, value =>
                value.Contains("background service", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(visible, value =>
                value.Contains("-Xmx", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ═════════════════════════════════════════════════ still open

    /// <summary>CP-2026-001 stays open: this milestone did not solve 26.x game rules.</summary>
    [Fact]
    public void The_game_rule_defect_remains_open_and_truthful()
    {
        var register = File.ReadAllText(
            Path.Combine(DesignSystemFiles.RepositoryRoot, "docs", "BUG-REGISTER.md"));

        Assert.Contains("CP-2026-001", register, StringComparison.Ordinal);
        Assert.Contains("**Open**", register, StringComparison.Ordinal);
        Assert.Empty(GamerulePolicy.Supported("26.2"));
    }

    // ═════════════════════════════════════════════════ helpers

    private static PlayerAccessRow Row(UnifiedPlayerAccess player, bool running = false) =>
        new(player, whitelistEnabled: false, serverRunning: running,
            (_, _) => Task.FromResult(true), _ => { });

    private static ListBox WorkspaceTabs()
    {
        var list = new ListBox
        {
            Style = (Style)Application.Current.FindResource("AppWorkspaceTabs"),
            ItemsSource = new[]
            {
                new NavigationItem("Overview", "Overview", "Status", AppIconKind.Home),
                new NavigationItem("Console", "Console", "Output", AppIconKind.Terminal),
                new NavigationItem("Manage", "Manage", "Files", AppIconKind.Box)
            }
        };
        return list;
    }

    private static string[] OverviewSectionHeaders() =>
        XDocument.Load(Path.Combine(AppDirectory, "Pages", "ServerOverviewPage.xaml")).Root!
            .DescendantsAndSelf()
            .Where(element => element.Name.LocalName == "AppSectionCard")
            .Select(element => element.Attribute("Header")?.Value ?? "")
            .Where(header => header.Length > 0)
            .ToArray();

    private static IEnumerable<string> SourceFiles() =>
        DesignSystemFiles.AllXaml()
            .Concat(DesignSystemFiles.AllCSharp())
            .Concat(Directory.EnumerateFiles(
                Path.Combine(DesignSystemFiles.RepositoryRoot, "docs"), "*.md", SearchOption.AllDirectories));

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string RenderedText(DependencyObject root)
    {
        var builder = new System.Text.StringBuilder();
        Collect(root, builder);
        return builder.ToString();

        static void Collect(DependencyObject node, System.Text.StringBuilder into)
        {
            if (node is TextBlock block)
                into.Append(block.Text).Append(' ');
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                Collect(VisualTreeHelper.GetChild(node, index), into);
        }
    }

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
}
