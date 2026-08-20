using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using ChunkPilot.App;
using ChunkPilot.App.Access;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>
/// The defects the Vanilla workspace stabilization pass removed, pinned so they cannot return.
/// </summary>
/// <remarks>
/// Each test names the thing that was wrong rather than the code that was changed: a boolean rendered
/// in front of a file name, a hint the caret sat inside, an exclusion pattern that did not match at
/// depth, a check box that could not be clicked. Copy is asserted only where the wording is the
/// contract - a column heading, a control label, a unit.
/// </remarks>
public sealed class VanillaWorkspaceStabilizationTests
{
    private static readonly string AppDirectory = DesignSystemFiles.AppProjectDirectory;
    private static readonly Guid ServerId = Guid.Parse("7f1c2f3a-0000-4000-8000-000000000001");

    // ═══════════════════════════════════════════════════ 1. window chrome

    /// <summary>
    /// The dark caption lives in one shared place, so a new window cannot ship with a light strip.
    /// </summary>
    [Fact]
    public void The_dark_title_bar_is_applied_by_one_shared_helper()
    {
        var chrome = File.ReadAllText(Path.Combine(AppDirectory, "DesignSystem", "AppWindowChrome.cs"));
        var theme = File.ReadAllText(Path.Combine(AppDirectory, "DesignSystem", "AppTheme.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.xaml.cs"));

        Assert.Contains("DwmSetWindowAttribute", chrome, StringComparison.Ordinal);
        Assert.Contains("AppWindowChrome.Apply(window)", theme, StringComparison.Ordinal);
        Assert.Contains("AppWindowChrome.Apply(this)", mainWindow, StringComparison.Ordinal);
        // The private copy that left the Create Server window light is gone.
        Assert.DoesNotContain("DwmSetWindowAttribute", mainWindow, StringComparison.Ordinal);

        var others = DesignSystemFiles.AllCSharp()
            .Where(path => !path.EndsWith("AppWindowChrome.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("DWMWA_USE_IMMERSIVE", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(others);
    }

    /// <summary>Applying the chrome to a real window succeeds and keeps the standard controls.</summary>
    [Fact]
    public void The_shared_chrome_applies_to_a_real_window_without_replacing_its_controls()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var window = new Window { Width = 400, Height = 300, ShowInTaskbar = false };
            try
            {
                AppWindowChrome.Apply(window);
                window.Show();

                Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                Assert.False(window.AllowsTransparency);
                Assert.False(window.Topmost);
                Assert.NotNull(window.Icon);
                // Windows 11 accepts the attribute; an older build would refuse it and keep its own
                // caption, which is why this is reported rather than assumed.
                Assert.True(AppWindowChrome.IsDarkCaptionApplied(window) ||
                            AppTheme.IsHighContrastPreferred);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ═══════════════════════════════════════════════════ 2. name field, watermark and caret

    /// <summary>Create Server starts blank, with a watermark that names the field.</summary>
    [Fact]
    public void The_server_name_field_starts_blank_with_a_watermark_and_no_example_name()
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml"));

        Assert.Contains("ds:AppInput.Placeholder=\"Server name\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Sunday survival", xaml, StringComparison.Ordinal);

        var console = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerConsolePage.xaml"));
        Assert.Contains("ds:AppInput.Placeholder=\"Type a command\"", console, StringComparison.Ordinal);
        Assert.DoesNotContain("Placeholder=\"Command sent to the server\"", console, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runtime evidence for the caret complaint: the hint is what the caret appeared to sit inside.
    /// </summary>
    /// <remarks>
    /// With a hint drawn from the same origin as the text, an empty focused field puts the caret at
    /// x=0 - correct, and immediately in front of wording nobody can edit. The hint now goes on focus,
    /// so the only thing at the insertion point is the caret. Both halves are asserted: the hint is
    /// visible when unfocused and gone when focused.
    /// </remarks>
    [Fact]
    public void The_hint_disappears_on_focus_so_nothing_sits_at_the_insertion_point()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var box = new TextBox { Width = 320 };
            AppInput.SetPlaceholder(box, "Server name");
            var window = new Window
            {
                Width = 420,
                Height = 200,
                ShowInTaskbar = false,
                Content = box
            };
            try
            {
                window.Show();
                box.UpdateLayout();
                var hint = FindByName<TextBlock>(box, "Placeholder");
                Assert.NotNull(hint);
                Assert.Equal(Visibility.Visible, hint!.Visibility);

                // Headless WPF processes are not guaranteed permission to take OS keyboard focus.
                // Verify the real template contract instead: the hint's base state is collapsed and
                // the only rule that shows it requires an empty, unfocused field.
                Assert.True(box.Focusable);
                Assert.True(KeyboardNavigation.GetIsTabStop(box));

                var inputs = XDocument.Load(Path.Combine(
                    AppDirectory, "Themes", "Controls", "Inputs.xaml"));
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var appTextBox = Assert.Single(
                    inputs.Descendants().Where(element => element.Name.LocalName == "Style"),
                    style => style.Attribute(x + "Key")?.Value == "AppTextBox");
                var placeholder = Assert.Single(
                    appTextBox.Descendants().Where(element => element.Name.LocalName == "TextBlock"),
                    textBlock => textBlock.Attribute(x + "Name")?.Value == "Placeholder");
                Assert.Equal("Collapsed", placeholder.Attribute("Visibility")?.Value);

                var showHint = Assert.Single(
                    box.Template.Triggers.OfType<MultiTrigger>(),
                    trigger =>
                        trigger.Conditions.Cast<Condition>().Any(condition =>
                            condition.Property == TextBox.TextProperty &&
                            Equals(condition.Value, "")) &&
                        trigger.Conditions.Cast<Condition>().Any(condition =>
                            condition.Property == UIElement.IsKeyboardFocusWithinProperty &&
                            Equals(condition.Value, false)));
                var visibility = Assert.Single(
                    showHint.Setters.OfType<Setter>(),
                    setter =>
                        setter.TargetName == "Placeholder" &&
                        setter.Property == UIElement.VisibilityProperty);
                Assert.Equal(Visibility.Visible, visibility.Value);

                // Typing puts the caret where the glyphs are, and Home and End reach the real ends.
                box.Text = "Weeknight world";
                box.CaretIndex = box.Text.Length;
                box.UpdateLayout();
                var start = box.GetRectFromCharacterIndex(0, true);
                var end = box.GetRectFromCharacterIndex(box.Text.Length, true);
                Assert.True(end.Left > start.Left);
                box.CaretIndex = 0;
                Assert.Equal(start.Left, box.GetRectFromCharacterIndex(box.CaretIndex, true).Left, 3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The hint is never stored as text, so an untouched field is genuinely empty.</summary>
    [Fact]
    public void The_watermark_is_never_stored_as_the_value()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var box = new TextBox();
            AppInput.SetPlaceholder(box, "Server name");
            Assert.Equal("", box.Text);
        });
    }

    // ═══════════════════════════════════════════════════ 3. EULA copy

    /// <summary>Compact EULA copy, with the acceptance itself untouched.</summary>
    [Fact]
    public void The_eula_section_is_compact_and_still_requires_a_deliberate_acceptance()
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppDirectory, "CreateServerLive", "CreateServerLiveWindow.xaml"));

        Assert.Contains("Header=\"Minecraft EULA\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Accept the Minecraft EULA", xaml, StringComparison.Ordinal);
        Assert.Contains("View EULA", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Required by Mojang", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Opening the document does not accept it", xaml, StringComparison.Ordinal);
        // The check box is never pre-checked in XAML, and the evidence line stays.
        Assert.DoesNotContain("IsChecked=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("EulaAcceptedDetail", xaml, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════ 4. file list and editor

    /// <summary>
    /// The file list shows names, not a rendered boolean.
    /// </summary>
    /// <remarks>
    /// "Falseeula.txt" was a TextBlock bound straight to <c>IsDirectory</c>, sitting immediately before
    /// the name. The kind is an icon now, and the only text in the row is the file name.
    /// </remarks>
    [Fact]
    public void The_file_list_renders_exact_names_and_never_a_boolean()
    {
        var root = XDocument.Load(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml")).Root!;
        var textBindings = root.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => element.Attribute("Text")?.Value ?? "")
            .ToArray();

        Assert.DoesNotContain("{Binding IsDirectory}", textBindings);
        Assert.Contains(textBindings, value => value == "{Binding Name}");
        // The kind is conveyed by an icon whose Kind is chosen by a trigger, not by text.
        var xaml = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"));
        Assert.Contains("<DataTrigger Binding=\"{Binding IsDirectory}\" Value=\"True\">", xaml, StringComparison.Ordinal);
    }

    /// <summary>"Save atomically" was implementation language on a button.</summary>
    [Fact]
    public void The_editor_save_action_is_named_for_what_the_user_is_doing()
    {
        // Attribute values only: a comment explaining the old wording is not the old wording.
        var content = AttributeValues(
            Path.Combine(AppDirectory, "Pages", "ServerManagePage.xaml"), "Content");

        Assert.Contains("Save changes", content);
        Assert.DoesNotContain("Save atomically", content);
    }

    /// <summary>Every selection lands in exactly one explicit state.</summary>
    [Theory]
    [InlineData("eula.txt", false, 12L, FileEditorState.Text)]
    [InlineData("server.jar", false, 52_000_000L, FileEditorState.Binary)]
    [InlineData("world", true, 0L, FileEditorState.Folder)]
    [InlineData("latest.log", false, 40L * 1024 * 1024, FileEditorState.TooLarge)]
    public async Task Selecting_an_entry_produces_the_state_that_matches_it(
        string name, bool isDirectory, long size, FileEditorState expected)
    {
        var client = new WorkspaceFakeClient { FileContent = "text" };
        var model = await ReadyModelAsync(client);

        model.SelectedFileEntry = new FileSystemEntry
        {
            Name = name,
            RelativePath = name,
            IsDirectory = isDirectory,
            SizeBytes = size,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await Settle();

        Assert.Equal(expected, model.EditorState);
        if (expected == FileEditorState.Binary)
        {
            Assert.Contains("not a text file", model.EditorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("", model.EditorContent);
        }
    }

    /// <summary>An empty file says so rather than looking like a failure to load.</summary>
    [Fact]
    public async Task An_empty_text_file_is_explained_and_still_editable()
    {
        var client = new WorkspaceFakeClient { FileContent = "" };
        var model = await ReadyModelAsync(client);

        model.SelectedFileEntry = TextEntry();
        await Settle();

        Assert.Equal(FileEditorState.Empty, model.EditorState);
        Assert.True(model.IsEditorEditable);
        Assert.Contains("empty", model.EditorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A read failure is reported in the service's own words.</summary>
    [Fact]
    public async Task A_failed_read_shows_the_reason_instead_of_a_blank_editor()
    {
        var client = new WorkspaceFakeClient { ReadFailure = "This file type is not treated as editable text." };
        var model = await ReadyModelAsync(client);

        model.SelectedFileEntry = TextEntry();
        await Settle();

        Assert.Equal(FileEditorState.Error, model.EditorState);
        Assert.Contains("not treated as editable text", model.EditorMessage, StringComparison.Ordinal);
    }

    /// <summary>Save is offered only for a real change, and a failure keeps the edit.</summary>
    [Fact]
    public async Task Save_needs_a_real_change_and_a_failed_save_keeps_the_users_text()
    {
        var client = new WorkspaceFakeClient { FileContent = "motd=hello" };
        var model = await ReadyModelAsync(client);
        model.SelectedFileEntry = TextEntry();
        await Settle();

        Assert.False(model.IsEditorDirty);
        Assert.False(model.SaveFileCommand.CanExecute(null));

        model.EditorContent = "motd=changed";
        Assert.True(model.IsEditorDirty);
        Assert.True(model.SaveFileCommand.CanExecute(null));

        client.WriteFailure = "The file changed outside ChunkPilot after it was opened.";
        await model.SaveFileCommand.ExecuteAsync(null);

        Assert.Equal("motd=changed", model.EditorContent);
        Assert.Contains("changed outside ChunkPilot", model.EditorSaveError, StringComparison.Ordinal);
        Assert.Equal("", model.EditorSavedNotice);

        client.WriteFailure = null;
        client.FileContent = "motd=changed";
        await model.SaveFileCommand.ExecuteAsync(null);

        Assert.Contains("Saved", model.EditorSavedNotice, StringComparison.Ordinal);
        Assert.Equal("", model.EditorSaveError);
        Assert.False(model.SaveFileCommand.CanExecute(null));
    }

    // ═══════════════════════════════════════════════════ 5. player access

    /// <summary>Headings are full words, and the columns size themselves to fit them.</summary>
    [Fact]
    public void Access_columns_cannot_clip_their_own_headings()
    {
        var path = Path.Combine(AppDirectory, "Pages", "ServerAccessPage.xaml");
        var xaml = File.ReadAllText(path);
        var root = XDocument.Load(path).Root!;

        Assert.Contains("Text=\"Whitelisted\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Operator\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Status\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.IsSharedSizeScope=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SharedSizeGroup=\"AccessWhitelist\"", xaml, StringComparison.Ordinal);

        // The duplicate management pad is gone, and no fixed-height table traps the wheel.
        var headings = root.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "Text" or "Content" or "Header")
            .Select(attribute => attribute.Value)
            .ToArray();
        string[] retiredLabels =
            ["OPERATORS & BANS", "Grant OP", "Remove OP", "Ban player", "Pardon player",
             "Enable whitelist", "Disable whitelist"];
        Assert.DoesNotContain(headings, value =>
            retiredLabels.Contains(value, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(root.Descendants(), element => element.Name.LocalName == "DataGrid");

        // Player state is never a shared read-only table cell again: the permission controls are
        // real, two-way check boxes. The polish pass moved them off the switch style; what matters
        // here is that they are operable and bound, not which boolean control draws them.
        Assert.Contains("IsChecked=\"{Binding Whitelisted, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding Operator, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>The online count and the slot count are two statements, not one fraction.</summary>
    [Fact]
    public async Task Online_players_and_slots_are_reported_separately()
    {
        var client = new WorkspaceFakeClient
        {
            Access = new PlayerAccessSnapshot
            {
                ServerId = ServerId,
                ServerRunning = true,
                OnlineCount = 1,
                MaxPlayers = 10,
                Stamp = "a",
                Players =
                [
                    new UnifiedPlayerAccess { Name = "Xustar", Operator = true, Online = true },
                    new UnifiedPlayerAccess { Name = "Traffic_Tom", Whitelisted = true }
                ]
            }
        };
        var model = await ReadyModelAsync(client);

        Assert.Equal("1 player online", model.OnlineCountText);
        Assert.Equal("10 slots", model.SlotCountText);
        Assert.Equal(2, model.PlayerRows.Count);
        Assert.Equal("Online", model.PlayerRows[0].StatusText);
        Assert.StartsWith("Offline", model.PlayerRows[1].StatusText, StringComparison.Ordinal);
    }

    /// <summary>A refused change reverts the switch and shows what the server said.</summary>
    [Fact]
    public async Task A_refused_operator_change_reverts_the_switch_and_reports_the_reason()
    {
        var client = new WorkspaceFakeClient
        {
            Access = RunningAccess(new UnifiedPlayerAccess { Name = "Xustar" }),
            ModerationFailure = "That player does not exist"
        };
        var model = await ReadyModelAsync(client);
        var row = Assert.Single(model.PlayerRows);

        row.Operator = true;
        await Settle();

        Assert.False(row.Operator);
        Assert.Contains("does not exist", row.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(row.IsPending);
        Assert.Equal(PlayerModerationAction.GrantOperator, client.LastModeration!.Action);
    }

    /// <summary>A confirmed change re-reads authoritative state rather than trusting the click.</summary>
    [Fact]
    public async Task A_confirmed_whitelist_change_reloads_state_from_the_server()
    {
        var client = new WorkspaceFakeClient
        {
            Access = RunningAccess(new UnifiedPlayerAccess { Name = "Traffic_Tom" })
        };
        var model = await ReadyModelAsync(client);
        var row = Assert.Single(model.PlayerRows);
        var readsBefore = client.AccessReads;

        client.Access = RunningAccess(new UnifiedPlayerAccess { Name = "Traffic_Tom", Whitelisted = true });
        row.Whitelisted = true;
        await Settle();

        Assert.True(row.Whitelisted);
        Assert.Equal("", row.ErrorMessage);
        Assert.True(client.AccessReads > readsBefore);
    }

    /// <summary>Ban is an intentionally direct moderation action; the destructive label is enough.</summary>
    [Fact]
    public async Task Banning_applies_immediately_without_a_confirmation_dialog()
    {
        var client = new WorkspaceFakeClient
        {
            Access = RunningAccess(new UnifiedPlayerAccess { Name = "Xustar", Online = true })
        };
        var dialogs = new RecordingDialogs { ConfirmResult = false };
        var model = await ReadyModelAsync(client, dialogs);
        var row = Assert.Single(model.PlayerRows);

        await row.BanCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.ConfirmTitles);
        Assert.Equal(PlayerModerationAction.Ban, client.LastModeration!.Action);
    }

    /// <summary>Pardon needs no confirmation: it restores access rather than removing it.</summary>
    [Fact]
    public async Task Pardon_applies_immediately()
    {
        var client = new WorkspaceFakeClient
        {
            Access = RunningAccess(new UnifiedPlayerAccess { Name = "Xustar", PlayerBanned = true })
        };
        var dialogs = new RecordingDialogs { ConfirmResult = false };
        var model = await ReadyModelAsync(client, dialogs);
        var row = Assert.Single(model.PlayerRows);

        Assert.True(row.CanPardon);
        Assert.False(row.CanBan);
        await row.PardonCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.ConfirmTitles);
        Assert.Equal(PlayerModerationAction.Pardon, client.LastModeration!.Action);
    }

    /// <summary>Adding a whitelist player refreshes the list rather than leaving it stale.</summary>
    [Fact]
    public async Task Adding_a_whitelist_player_refreshes_the_displayed_state()
    {
        var client = new WorkspaceFakeClient { Access = RunningAccess() };
        var model = await ReadyModelAsync(client);

        Assert.False(model.AddWhitelistPlayerCommand.CanExecute(null));
        model.NewWhitelistPlayerName = "New_Player";
        Assert.True(model.AddWhitelistPlayerCommand.CanExecute(null));

        client.Access = RunningAccess(new UnifiedPlayerAccess { Name = "New_Player", Whitelisted = true });
        await model.AddWhitelistPlayerCommand.ExecuteAsync(null);

        Assert.Equal("", model.NewWhitelistPlayerName);
        var row = Assert.Single(model.PlayerRows);
        Assert.Equal("New_Player", row.Name);
        Assert.True(row.Whitelisted);
    }

    /// <summary>A moderation command typed into the Console reaches the Access page.</summary>
    [Fact]
    public async Task An_operator_change_made_through_the_console_refreshes_access()
    {
        var client = new WorkspaceFakeClient { Access = RunningAccess() };
        var model = await ReadyModelAsync(client);

        client.Access = RunningAccess(new UnifiedPlayerAccess { Name = "Xustar", Operator = true });
        model.ConsoleCommand = "op Xustar";
        await model.SendConsoleCommandCommand.ExecuteAsync(null);
        await Settle();

        var row = Assert.Single(model.PlayerRows);
        Assert.True(row.Operator);
    }

    /// <summary>An unrelated console command does not trigger access or gamerule reads.</summary>
    [Fact]
    public async Task An_unrelated_console_command_triggers_no_extra_reads()
    {
        var client = new WorkspaceFakeClient { Access = RunningAccess() };
        var model = await ReadyModelAsync(client);
        var accessReads = client.AccessReads;
        var gameruleReads = client.GameruleReads;

        model.ConsoleCommand = "say hello";
        await model.SendConsoleCommandCommand.ExecuteAsync(null);
        await Settle();

        Assert.Equal(accessReads, client.AccessReads);
        Assert.Equal(gameruleReads, client.GameruleReads);
    }

    /// <summary>A stopped server offers no moderation controls and says why.</summary>
    [Fact]
    public async Task A_stopped_server_disables_moderation_and_explains_it()
    {
        var client = new WorkspaceFakeClient
        {
            Access = new PlayerAccessSnapshot
            {
                ServerId = ServerId,
                ServerRunning = false,
                Stamp = "stopped",
                Players = [new UnifiedPlayerAccess { Name = "Xustar", Whitelisted = true }]
            }
        };
        var model = await ReadyModelAsync(client);

        Assert.False(model.CanModeratePlayers);
        Assert.Contains("running server", model.PlayerEmptyStateMessage, StringComparison.OrdinalIgnoreCase);
        var row = Assert.Single(model.PlayerRows);
        Assert.False(row.IsInteractive);
        Assert.False(row.CanKick);
        Assert.False(row.CanBan);
    }

    /// <summary>Moderation commands and their confirming replies are one shared rule.</summary>
    [Theory]
    [InlineData(PlayerModerationAction.AddToWhitelist, "whitelist add Xustar", "Added Xustar to the whitelist")]
    [InlineData(PlayerModerationAction.RemoveFromWhitelist, "whitelist remove Xustar", "Removed Xustar from the whitelist")]
    [InlineData(PlayerModerationAction.GrantOperator, "op Xustar", "Made Xustar a server operator")]
    [InlineData(PlayerModerationAction.RemoveOperator, "deop Xustar", "Made Xustar no longer a server operator")]
    [InlineData(PlayerModerationAction.Ban, "ban Xustar", "Banned Xustar: Banned by an operator")]
    [InlineData(PlayerModerationAction.Pardon, "pardon Xustar", "Unbanned Xustar")]
    [InlineData(PlayerModerationAction.Kick, "kick Xustar", "Kicked Xustar: Kicked by an operator")]
    public void Each_moderation_action_has_one_command_and_one_confirming_reply(
        PlayerModerationAction action, string expectedCommand, string reply)
    {
        Assert.Equal(expectedCommand, PlayerModerationPolicy.CommandFor(action, "Xustar"));
        Assert.True(PlayerModerationPolicy.IsSuccessReply(action, "Xustar", reply));
        Assert.False(PlayerModerationPolicy.IsFailureReply(action, "Xustar", reply));
        Assert.False(PlayerModerationPolicy.IsSuccessReply(action, "Xustar",
            "[19:55:01] [Server thread/INFO]: That player does not exist"));
        Assert.True(PlayerModerationPolicy.IsFailureReply(action, "Xustar",
            "[19:55:01] [Server thread/INFO]: That player does not exist"));
    }

    /// <summary>Granting and removing operator must not both match the same reply.</summary>
    [Fact]
    public void Operator_replies_are_not_confused_with_each_other()
    {
        const string granted = "Made Xustar a server operator";
        const string removed = "Made Xustar no longer a server operator";

        Assert.True(PlayerModerationPolicy.IsSuccessReply(PlayerModerationAction.GrantOperator, "Xustar", granted));
        Assert.False(PlayerModerationPolicy.IsSuccessReply(PlayerModerationAction.GrantOperator, "Xustar", removed));
        Assert.True(PlayerModerationPolicy.IsSuccessReply(PlayerModerationAction.RemoveOperator, "Xustar", removed));
        Assert.False(PlayerModerationPolicy.IsSuccessReply(PlayerModerationAction.RemoveOperator, "Xustar", granted));
    }

    /// <summary>A reason cannot smuggle a second console command onto the line.</summary>
    [Fact]
    public void A_ban_reason_cannot_contain_control_characters()
    {
        var command = PlayerModerationPolicy.CommandFor(
            PlayerModerationAction.Ban, "Xustar", "griefing\nop Attacker");

        Assert.DoesNotContain('\n', command);
        Assert.DoesNotContain('\r', command);
        Assert.Contains("griefing", command, StringComparison.Ordinal);
    }

    /// <summary>Only the commands that change something trigger a re-read.</summary>
    [Theory]
    [InlineData("op Someone", true, false)]
    [InlineData("deop Someone", true, false)]
    [InlineData("whitelist add Someone", true, false)]
    [InlineData("ban Someone", true, false)]
    [InlineData("pardon Someone", true, false)]
    [InlineData("kick Someone", true, false)]
    [InlineData("/op Someone", true, false)]
    [InlineData("gamerule keepInventory true", false, true)]
    [InlineData("say hello", false, false)]
    [InlineData("time set day", false, false)]
    public void Console_commands_are_classified_by_what_they_change(
        string command, bool access, bool gamerules)
    {
        Assert.Equal(access, PlayerModerationPolicy.AffectsPlayerAccess(command));
        Assert.Equal(gamerules, PlayerModerationPolicy.AffectsGamerules(command));
    }

    // ═══════════════════════════════════════════════════ 6. game rules

    /// <summary>Rules are presented with real controls and honest provenance.</summary>
    [Fact]
    public async Task Game_rules_present_a_switch_a_number_box_and_where_the_value_came_from()
    {
        var client = new WorkspaceFakeClient
        {
            Gamerules = new GameruleStateResponse
            {
                ServerId = ServerId,
                ServerRunning = true,
                CanChange = true,
                Rules =
                [
                    new GameruleState
                    {
                        Name = "keepInventory", Label = "Keep items on death",
                        Kind = GameruleValueKind.Boolean, Value = "false",
                        Provenance = GameruleProvenance.ReportedByServer
                    },
                    new GameruleState
                    {
                        Name = "randomTickSpeed", Label = "Random tick speed",
                        Kind = GameruleValueKind.WholeNumber, Value = "3",
                        Minimum = 0, Maximum = 100,
                        Provenance = GameruleProvenance.ReportedByServer
                    }
                ]
            }
        };
        var model = await ReadyModelAsync(client);

        Assert.True(model.ShowsGamerules);
        var boolean = model.Gamerules.Single(rule => rule.Name == "keepInventory");
        var number = model.Gamerules.Single(rule => rule.Name == "randomTickSpeed");

        Assert.True(boolean.IsBoolean);
        Assert.False(boolean.BooleanValue);
        Assert.True(number.IsInteger);
        Assert.Equal(3, number.IntegerValue);
        Assert.Equal("Reported by the server", boolean.ProvenanceText);
        Assert.True(boolean.IsEnabled);
    }

    /// <summary>Changing a rule sends one command and takes the server's value back.</summary>
    [Fact]
    public async Task Changing_a_boolean_rule_sends_one_command_and_adopts_the_reported_value()
    {
        var client = new WorkspaceFakeClient { Gamerules = RunningGamerules("false") };
        var model = await ReadyModelAsync(client);
        var rule = Assert.Single(model.Gamerules);

        client.Gamerules = RunningGamerules("true");
        rule.BooleanValue = true;
        await Settle();

        Assert.True(rule.BooleanValue);
        Assert.Equal(1, client.GameruleApplies);
        Assert.Equal("true", client.LastGameruleValue);
        Assert.False(rule.IsPending);
    }

    /// <summary>A refused rule change reverts rather than leaving a switch that lies.</summary>
    [Fact]
    public async Task A_refused_rule_change_reverts_the_control()
    {
        var client = new WorkspaceFakeClient
        {
            Gamerules = RunningGamerules("false"),
            GameruleFailure = "Gamerules are applied to a running server."
        };
        var model = await ReadyModelAsync(client);
        var rule = Assert.Single(model.Gamerules);

        rule.BooleanValue = true;
        await Settle();

        Assert.False(rule.BooleanValue);
        Assert.False(rule.IsPending);
    }

    /// <summary>A stopped server explains when the controls become available.</summary>
    [Fact]
    public async Task A_stopped_server_explains_when_game_rules_become_available()
    {
        var client = new WorkspaceFakeClient
        {
            Gamerules = new GameruleStateResponse
            {
                ServerId = ServerId,
                ServerRunning = false,
                CanChange = false,
                UnavailableReason = "Game rules are read from the running server. Start the server to see and change them."
            }
        };
        var model = await ReadyModelAsync(client);

        Assert.False(model.ShowsGamerules);
        Assert.True(model.ShowsGameruleUnavailable);
        Assert.Contains("Start the server", model.GameruleUnavailableReason, StringComparison.Ordinal);
    }

    /// <summary>A gamerule command typed into the Console refreshes the page.</summary>
    [Fact]
    public async Task A_gamerule_command_in_the_console_refreshes_the_rules()
    {
        var client = new WorkspaceFakeClient { Gamerules = RunningGamerules("false") };
        var model = await ReadyModelAsync(client);

        client.Gamerules = RunningGamerules("true");
        model.ConsoleCommand = "gamerule keepInventory true";
        await model.SendConsoleCommandCommand.ExecuteAsync(null);
        await Settle();

        Assert.True(Assert.Single(model.Gamerules).BooleanValue);
    }

    /// <summary>
    /// The preset selector and its raw enum names are gone from the interface and from the model.
    /// </summary>
    /// <remarks>
    /// Checked against bindings and code rather than raw file text, so a comment recording what was
    /// removed does not read as the thing itself.
    /// </remarks>
    [Fact]
    public void Gameplay_presets_are_no_longer_exposed_anywhere()
    {
        string[] gone = ["GameplayPreset", "NormalSurvival", "TechnicalVanilla", "RelaxedFriendsServer"];

        foreach (var file in DesignSystemFiles.AllXaml())
        {
            var root = XDocument.Load(file).Root;
            if (root is null)
                continue;
            var values = root.DescendantsAndSelf()
                .SelectMany(element => element.Attributes())
                .Select(attribute => attribute.Value)
                .ToArray();
            Assert.DoesNotContain(values, value =>
                gone.Any(name => value.Contains(name, StringComparison.Ordinal)));
        }

        foreach (var file in DesignSystemFiles.AllCSharp())
        {
            var code = string.Join('\n', File.ReadAllLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));
            foreach (var name in gone)
                Assert.DoesNotContain(name, code, StringComparison.Ordinal);
        }

        // And the policy itself no longer exists in Core.
        Assert.Null(Type.GetType("ChunkPilot.Core.GameplayPresetPolicy, ChunkPilot.Core"));
    }

    // ═══════════════════════════════════════════════════ 7. memory in GB

    /// <summary>Memory is presented in GB while the stored units stay MiB.</summary>
    [Fact]
    public async Task Memory_is_shown_in_gigabytes_and_stored_in_mebibytes()
    {
        var client = new WorkspaceFakeClient();
        var model = await ReadyModelAsync(client);

        model.MinimumRamMb = 1_024;
        model.MaximumRamMb = 4_096;
        Assert.Equal(1d, model.MinimumMemoryGb);
        Assert.Equal(4d, model.MaximumMemoryGb);

        model.MaximumMemoryGb = 6;
        Assert.Equal(6_144, model.MaximumRamMb);
        Assert.Equal(6d, model.MaximumMemoryGb);

        // Half gigabytes are expressible.
        model.MinimumMemoryGb = 1.5;
        Assert.Equal(1_536, model.MinimumRamMb);
        Assert.Equal(1.5d, model.MinimumMemoryGb);

        Assert.Contains("-Xms1536M", model.MemoryDetailText, StringComparison.Ordinal);
        Assert.Contains("-Xmx6144M", model.MemoryDetailText, StringComparison.Ordinal);
    }

    /// <summary>An unusual saved allocation is shown without being quietly re-rounded.</summary>
    [Fact]
    public async Task An_existing_configuration_is_displayed_without_changing_its_allocation()
    {
        var client = new WorkspaceFakeClient();
        var model = await ReadyModelAsync(client);

        model.MaximumRamMb = 3_000;
        var displayed = model.MaximumMemoryGb;

        // Reading the value and writing the same displayed choice back leaves the bytes alone.
        model.MaximumMemoryGb = displayed;
        Assert.Equal(3_000, model.MaximumRamMb);
        Assert.Contains(displayed, model.MemoryChoices);
    }

    /// <summary>Minimum can never exceed maximum, whichever one the user moves.</summary>
    [Fact]
    public async Task Minimum_memory_cannot_exceed_maximum()
    {
        var client = new WorkspaceFakeClient();
        var model = await ReadyModelAsync(client);
        model.MinimumRamMb = 1_024;
        model.MaximumRamMb = 2_048;

        model.MinimumMemoryGb = 8;
        Assert.True(model.MaximumRamMb >= model.MinimumRamMb);

        model.MaximumMemoryGb = 1;
        Assert.True(model.MinimumRamMb <= model.MaximumRamMb);
    }

    /// <summary>The labels state the unit, and megabytes are gone from the page.</summary>
    [Fact]
    public void The_memory_labels_state_gigabytes()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerSettingsPage.xaml"));

        Assert.Contains("Minimum memory (GB)", xaml, StringComparison.Ordinal);
        Assert.Contains("Maximum memory (GB)", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Min RAM (MB)", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Max RAM (MB)", xaml, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════ 8. scroll bars

    /// <summary>The grab target is generous; the drawn pill stays thin.</summary>
    [Fact]
    public void The_page_scroll_bar_target_is_at_least_twenty_four_pixels_wide()
    {
        var metrics = XDocument.Load(Path.Combine(
            DesignSystemFiles.ThemesDirectory, "Tokens", "MetricTokens.xaml")).Root!;

        var hit = Value(metrics, "AppScrollBarHitWidth");
        var pill = Value(metrics, "AppScrollBarThumbThickness");

        Assert.True(hit >= 24, $"The page scroll bar target is only {hit} dip wide.");
        Assert.True(pill <= 6, "The drawn pill must stay thin.");
        Assert.True(pill >= 3, "The pill must remain visible.");
    }

    /// <summary>
    /// The bar is drawn over the content's edge, so a page's width does not change when it appears.
    /// </summary>
    [Fact]
    public void The_page_scroll_bar_overlays_the_content_instead_of_shifting_it()
    {
        WpfDesignSystemHost.Run(() =>
        {
            var content = new Border { Height = 4_000 };
            var viewer = new ScrollViewer
            {
                Style = (Style)WpfDesignSystemHost.Resolve("AppPageScrollViewer")!,
                Content = content
            };
            var window = new Window { Width = 600, Height = 400, Content = viewer, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Equal(viewer.ActualWidth, viewer.ViewportWidth, 1);
                Assert.Equal(viewer.ActualWidth, content.ActualWidth, 1);

                var bar = FindDescendant<ScrollBar>(viewer);
                Assert.NotNull(bar);
                Assert.Equal(24d, bar!.ActualWidth, 1);

                // The pointer lands on the bar well inside the content area, not only on the pill.
                var thumb = FindDescendant<Thumb>(bar);
                Assert.NotNull(thumb);
                Assert.True(thumb!.ActualWidth <= 24);
                var hit = viewer.InputHitTest(new Point(viewer.ActualWidth - 20, viewer.ActualHeight / 2));
                Assert.NotNull(hit);
                Assert.True(IsWithin(hit as DependencyObject, bar),
                    "A click 20 dip from the edge did not reach the scroll bar.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ═══════════════════════════════════════════════════ 9. connection card

    /// <summary>The connection card states addresses and never claims reachability.</summary>
    [Fact]
    public void The_connection_card_aligns_its_values_and_keeps_its_two_negatives_distinct()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory, "Pages", "ServerOverviewPage.xaml"));

        Assert.Contains("Text=\"This device\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Local network\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Public access\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Reachability not verified\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding HasConfiguredPublicAddress, Converter={StaticResource BoolVisibility}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Header=\"Connect\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Test local connection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource AppAccentSurface}\"", xaml, StringComparison.Ordinal);
        // A long address trims with the full value in a tooltip rather than stretching the card.
        Assert.Contains("ToolTip=\"{Binding ServerLanAddress}\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>"Not configured" and "Not verified" mean different things and stay separate.</summary>
    [Fact]
    public async Task An_unconfigured_public_address_is_not_reported_as_unverified_only()
    {
        var client = new WorkspaceFakeClient();
        var model = await ReadyModelAsync(client);

        Assert.Equal("Not configured", model.ServerPublicAddress);
        Assert.Equal("localhost:25565", model.ServerLocalAddress);
    }

    // ═══════════════════════════════════════════════════ 10. typography

    /// <summary>
    /// Type reads heavier through contrast and the small optical face, never a synthetic weight.
    /// </summary>
    [Fact]
    public void Typography_gains_weight_without_inventing_a_font_weight()
    {
        var typography = File.ReadAllText(Path.Combine(
            DesignSystemFiles.ThemesDirectory, "Tokens", "TypographyTokens.xaml"));
        var text = File.ReadAllText(Path.Combine(
            DesignSystemFiles.ThemesDirectory, "Controls", "Text.xaml"));

        // No Medium token: Segoe UI Variable has no such face and a request for it resolves to SemiBold.
        Assert.DoesNotContain("AppFontWeightMedium", typography, StringComparison.Ordinal);
        Assert.Contains("AppFontFamilySmall", text, StringComparison.Ordinal);

        WpfDesignSystemHost.Run(() =>
        {
            var muted = new TextBlock { Style = (Style)WpfDesignSystemHost.Resolve("AppMutedText")! };
            var label = new TextBlock { Style = (Style)WpfDesignSystemHost.Resolve("AppLabelText")! };
            var body = new TextBlock { Style = (Style)WpfDesignSystemHost.Resolve("AppBodyText")! };

            Assert.Equal(FontWeights.Normal, muted.FontWeight);
            Assert.Equal(FontWeights.Normal, label.FontWeight);
            Assert.Equal(FontWeights.Normal, body.FontWeight);

            // Secondary and muted moved one step up the ramp; the steps stay distinct.
            var primary = Brush("AppTextPrimary");
            var secondary = Brush("AppTextSecondary");
            var mutedBrush = Brush("AppTextMuted");
            var tertiary = Brush("AppTextTertiary");
            var disabled = Brush("AppTextDisabled");

            Assert.True(Luminance(primary) > Luminance(secondary));
            Assert.True(Luminance(secondary) > Luminance(mutedBrush));
            Assert.True(Luminance(mutedBrush) > Luminance(tertiary));
            Assert.True(Luminance(tertiary) > Luminance(disabled));
        });
    }

    // ═══════════════════════════════════════════════════ 11. tray restoration

    /// <summary>
    /// One left click restores the existing window; a double click does not do it twice.
    /// </summary>
    [Fact]
    public void The_tray_icon_restores_on_a_single_left_click_and_never_twice()
    {
        var app = File.ReadAllText(Path.Combine(AppDirectory, "App.xaml.cs"));

        Assert.Contains("trayIcon.MouseClick", app, StringComparison.Ordinal);
        Assert.Contains("Forms.MouseButtons.Left", app, StringComparison.Ordinal);
        // The double-click-only handler that made the icon look unresponsive is gone.
        Assert.DoesNotContain("trayIcon.DoubleClick", app, StringComparison.Ordinal);
        // Restoration presents the existing shell through the shared helper, never a new window.
        Assert.Contains("WindowForegroundPresenter.Present(window)", app, StringComparison.Ordinal);
        Assert.Equal(1, app.Split("new MainWindow(", StringSplitOptions.None).Length - 1);
        // The icon goes as soon as the window closes.
        Assert.Contains("window.Closing += (_, _) => RemoveTrayIcon();", app, StringComparison.Ordinal);
        Assert.True(global::ChunkPilot.App.App.TrayClickCoalescingWindow > TimeSpan.Zero);
    }

    // ═══════════════════════════════════════════════════ helpers

    private static PlayerAccessSnapshot RunningAccess(params UnifiedPlayerAccess[] players) => new()
    {
        ServerId = ServerId,
        ServerRunning = true,
        OnlineCount = players.Count(player => player.Online),
        MaxPlayers = 10,
        Stamp = Guid.NewGuid().ToString("N"),
        Players = players
    };

    private static GameruleStateResponse RunningGamerules(string value) => new()
    {
        ServerId = ServerId,
        ServerRunning = true,
        CanChange = true,
        Rules =
        [
            new GameruleState
            {
                Name = "keepInventory",
                Label = "Keep items on death",
                Kind = GameruleValueKind.Boolean,
                Value = value,
                Provenance = GameruleProvenance.ReportedByServer
            }
        ]
    };

    private static FileSystemEntry TextEntry() => new()
    {
        Name = "server.properties",
        RelativePath = "server.properties",
        SizeBytes = 128,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    private static async Task<MainViewModel> ReadyModelAsync(
        WorkspaceFakeClient client,
        IDialogService? dialogs = null)
    {
        var model = new MainViewModel(client, dialogs ?? new RecordingDialogs());
        await model.InitializeAsync();
        model.SelectedServer = model.Servers.Single();
        await Settle();
        return model;
    }

    /// <summary>Lets the fire-and-forget loads the view model starts on selection complete.</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 8; i++)
            await Task.Yield();
        await Task.Delay(20);
    }

    private static string[] AttributeValues(string xamlFile, string attributeName) =>
        XDocument.Load(xamlFile).Root!
            .DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName == attributeName)
            .Select(attribute => attribute.Value)
            .ToArray();

    private static double Value(XElement root, string key) =>
        double.Parse(
            root.Elements()
                .First(element => element.Attribute(DesignSystemFiles.XamlNamespace + "Key")?.Value == key)
                .Value,
            System.Globalization.CultureInfo.InvariantCulture);

    private static Color Brush(string key) =>
        ((SolidColorBrush)WpfDesignSystemHost.Resolve(key)!).Color;

    private static double Luminance(Color color) =>
        0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;

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

    private static bool IsWithin(DependencyObject? candidate, DependencyObject ancestor)
    {
        while (candidate is not null)
        {
            if (ReferenceEquals(candidate, ancestor))
                return true;
            candidate = candidate is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(candidate)
                : null;
        }
        return false;
    }

    /// <summary>
    /// An Agent that answers the workspace operations, and records what it was asked to do.
    /// </summary>
    private sealed class WorkspaceFakeClient : IAgentClient
    {
        public PlayerAccessSnapshot Access { get; set; } = new();
        public GameruleStateResponse Gamerules { get; set; } = new();
        public string FileContent { get; set; } = "";
        public string? ReadFailure { get; set; }
        public string? WriteFailure { get; set; }
        public string? ModerationFailure { get; set; }
        public string? GameruleFailure { get; set; }

        public int AccessReads { get; private set; }
        public int GameruleReads { get; private set; }
        public int GameruleApplies { get; private set; }
        public string? LastGameruleValue { get; private set; }
        public PlayerModerationRequest? LastModeration { get; private set; }

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            switch (operation)
            {
                case "GetPlayerAccess":
                    AccessReads++;
                    return Task.FromResult((TResponse)(object)Access);
                case "ReadGamerules":
                    GameruleReads++;
                    return Task.FromResult((TResponse)(object)Gamerules);
                case "ModeratePlayer":
                    LastModeration = (PlayerModerationRequest)payload!;
                    return Task.FromResult((TResponse)(object)(ModerationFailure is null
                        ? OperationResult.Ok("Confirmed by the server.")
                        : OperationResult.Fail(ModerationFailure)));
                case "ApplyGamerules":
                    GameruleApplies++;
                    LastGameruleValue = ((GameruleApplyRequest)payload!).Changes.Values.First();
                    return Task.FromResult((TResponse)(object)(GameruleFailure is null
                        ? OperationResult.Ok("Gamerules applied live.")
                        : OperationResult.Fail(GameruleFailure)));
                case "ReadFile":
                    if (ReadFailure is not null)
                        throw new IOException(ReadFailure);
                    return Task.FromResult((TResponse)(object)new TextFileContent
                    {
                        RelativePath = ((FilesRequest)payload!).RelativePath,
                        Content = FileContent,
                        LoadedSha256 = "fixture"
                    });
                case "WriteFile":
                    if (WriteFailure is not null)
                        throw new IOException(WriteFailure);
                    return Task.FromResult((TResponse)(object)OperationResult.Ok("Written atomically."));
            }

            object response = operation switch
            {
                "Dashboard" => new DashboardSnapshot
                {
                    AgentConnected = true,
                    Host = new HostSnapshot
                    {
                        LanAddress = "10.0.0.140",
                        TotalMemoryBytes = 32L * 1024 * 1024 * 1024
                    },
                    Servers =
                    [
                        new ServerSnapshot
                        {
                            Definition = new ServerDefinition
                            {
                                Id = ServerId,
                                Name = "test survival",
                                RootPath = @"C:\fixture",
                                Executable = @"C:\fixture\java.exe",
                                WorkingDirectory = @"C:\fixture",
                                Port = 25565,
                                MinimumRamMb = 1_024,
                                MaximumRamMb = 4_096,
                                MinecraftVersion = "1.21.1"
                            },
                            State = Access.ServerRunning ? ServerState.Running : ServerState.Stopped,
                            PlayerAccessStamp = Access.Stamp
                        }
                    ]
                },
                "GetCapabilities" => new ServerCapabilityProfile { SupportsGamerules = true },
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
            return Task.FromResult((TResponse)response);
        }
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public List<string> ConfirmTitles { get; } = [];
        public string? LastError { get; private set; }

        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;

        public bool Confirm(string title, string message)
        {
            ConfirmTitles.Add(title);
            return ConfirmResult;
        }

        public void ShowError(string title, string message) => LastError = message;
        public void ShowInformation(string title, string message) { }
    }
}
