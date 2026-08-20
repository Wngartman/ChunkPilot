using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ChunkPilot.App;
using ChunkPilot.App.CreateServer;
using ChunkPilot.App.CreateServerLive;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests;

/// <summary>
/// Covers the live Vanilla wizard: its states, its consent rules, and what it may not do.
/// </summary>
/// <remarks>
/// <para>
/// The wizard's entire contact with the outside world is <see cref="IVanillaCreationGateway"/>, so a
/// fake gateway can reproduce every state a real Agent can produce — a stale cache, a version that
/// vanished, a cancellation that arrived too late, a rollback, an outcome that needs a person — and
/// each one is asserted here rather than hoped for at runtime.
/// </para>
/// <para>
/// The negative assertions matter as much as the positive ones: no plan reaches the Agent without
/// deliberate EULA acceptance, no second operation can be started by a second click, and the normal
/// Create Server path and retained development shortcut share the same live composition while the
/// synthetic preview remains isolated.
/// </para>
/// </remarks>
public sealed class CreateServerV2LiveVanillaTests
{
    private static readonly string LiveDirectory =
        Path.Combine(DesignSystemFiles.AppProjectDirectory, "CreateServerLive");

    private static readonly string WindowXamlFile =
        Path.Combine(LiveDirectory, "CreateServerLiveWindow.xaml");

    // ------------------------------------------------------------------ launch isolation

    [Fact]
    public void The_documented_switch_is_the_one_that_opens_the_live_wizard()
    {
        Assert.Equal("--create-server-v2-live-vanilla", CreateServerLiveLauncher.LiveVanillaSwitch);
        Assert.True(CreateServerLiveLauncher.IsRequested(["--create-server-v2-live-vanilla"]));
        Assert.True(CreateServerLiveLauncher.IsRequested(["--other", "--Create-Server-V2-Live-Vanilla"]));
    }

    [Fact]
    public void Normal_startup_and_the_synthetic_preview_never_request_the_live_wizard()
    {
        string[][] other =
        [
            [],
            ["--design-gallery"],
            ["--create-server-v2-preview"],
            ["--create-server-v2"],
            ["create-server-v2-live-vanilla"],
            ["C:\\some\\path.txt"]
        ];

        Assert.All(other, arguments => Assert.False(CreateServerLiveLauncher.IsRequested(arguments)));
    }

    [Fact]
    public void The_two_switches_are_distinct_and_neither_answers_for_the_other()
    {
        Assert.NotEqual(CreateServerPreviewLauncher.PreviewSwitch, CreateServerLiveLauncher.LiveVanillaSwitch);
        Assert.False(CreateServerPreviewLauncher.IsRequested([CreateServerLiveLauncher.LiveVanillaSwitch]));
        Assert.False(CreateServerLiveLauncher.IsRequested([CreateServerPreviewLauncher.PreviewSwitch]));
    }

    [Fact]
    public void The_live_wizard_opens_only_after_the_whole_normal_startup_has_run()
    {
        var startup = File.ReadAllText(Path.Combine(DesignSystemFiles.AppProjectDirectory, "App.xaml.cs"));
        var previewIndex = startup.IndexOf("CreateServerPreviewLauncher.TryRun", StringComparison.Ordinal);
        var mutexIndex = startup.IndexOf("new Mutex(", StringComparison.Ordinal);
        var agentIndex = startup.IndexOf("new AgentClient()", StringComparison.Ordinal);
        var showIndex = startup.IndexOf("window.Activate();", StringComparison.Ordinal);
        var liveIndex = startup.IndexOf("CreateServerLiveLauncher.IsRequested", StringComparison.Ordinal);

        Assert.True(liveIndex > 0, "App startup never offers the live switch.");
        // The preview replaces startup; the live wizard follows it. That ordering is the whole
        // difference between "cannot disturb anything" and "creates a real server".
        Assert.True(previewIndex < mutexIndex, "The preview must still run before the single-instance lock.");
        Assert.True(liveIndex > mutexIndex, "The live wizard must run after the single-instance lock.");
        Assert.True(liveIndex > agentIndex, "The live wizard must run after the agent client exists.");
        Assert.True(liveIndex > showIndex, "The live wizard must open after the shell window is shown.");
    }

    [Fact]
    public void Only_the_shell_composition_and_bootstrap_know_the_live_wizard_exists()
    {
        var offenders = DesignSystemFiles.AllCSharp()
            .Concat(DesignSystemFiles.AllXaml())
            .Where(file => !file.StartsWith(LiveDirectory, StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains("CreateServerLive", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["App.xaml.cs", "MainWindow.xaml.cs", "WebUiWindow.xaml.cs"], offenders);
    }

    [Fact]
    public void The_product_create_server_command_is_a_semantic_live_vanilla_request()
    {
        var model = new MainViewModel(null!, null!);
        var requests = 0;
        model.VanillaCreationRequested += (_, _) => requests++;

        model.CreateVanillaServerCommand.Execute(null);

        Assert.Equal(1, requests);
    }

    [Fact]
    public void The_synthetic_preview_still_reaches_no_agent_operation_and_shows_no_live_data()
    {
        var preview = new CreateServerPreviewViewModel();
        preview.SelectedIntent = CreationIntentCatalog.For(CreationIntent.Vanilla);
        preview.ServerName = "Sunday survival";
        preview.SelectedOption = preview.Options[0];
        preview.NextCommand.Execute(null);
        preview.NextCommand.Execute(null);
        preview.FinishPreviewCommand.Execute(null);

        Assert.Equal(CreationWizardStep.Completion, preview.CurrentStep);
        // The preview has no step in which anything is created, and no property that could hold an
        // acceptance or an operation.
        var members = typeof(CreateServerPreviewViewModel)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(member => member.Name)
            .ToArray();
        Assert.DoesNotContain(members, name => name.Contains("Eula", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, name => name.Contains("Operation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, name => name.Contains("Gateway", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_live_source_file_contains_the_synthetic_preview_catalogue()
    {
        // Prose may mention the preview; nothing may consume its invented data. The distinction is
        // the type name, which is the only way the synthetic catalogue can actually be reached.
        string[] forbidden = ["SyntheticPreviewCatalog", "SyntheticPreviewOption", "SyntheticPreviewProject"];
        var offenders = Directory.EnumerateFiles(LiveDirectory, "*.*", SearchOption.AllDirectories)
            .SelectMany(file => forbidden
                .Where(symbol => File.ReadAllText(file).Contains(symbol, StringComparison.Ordinal))
                .Select(symbol => $"{Path.GetFileName(file)} references {symbol}"))
            .ToArray();

        Assert.True(offenders.Length == 0, "Live files reference synthetic data: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_gateway_uses_only_named_pipe_operations_and_adds_no_transport()
    {
        var gateway = File.ReadAllText(Path.Combine(LiveDirectory, "VanillaCreationGateway.cs"));

        Assert.DoesNotContain("HttpClient", gateway, StringComparison.Ordinal);
        Assert.DoesNotContain("TcpClient", gateway, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpListener", gateway, StringComparison.Ordinal);
        Assert.Equal("VanillaVersions", AgentVanillaCreationGateway.Operations.Catalog);
        Assert.Equal("BeginVanillaCreation", AgentVanillaCreationGateway.Operations.Begin);
        Assert.Equal("InstallProgress", AgentVanillaCreationGateway.Operations.Progress);
        Assert.Equal("CancelInstall", AgentVanillaCreationGateway.Operations.Cancel);
    }

    [Fact]
    public void The_view_model_holds_no_provider_client_installer_or_store()
    {
        string[] forbidden =
        [
            "HttpClient", "ManagedServerInstaller", "ChunkPilotStore", "ServerCreationTransaction",
            "AgentClient", "ServerDownloadCatalog", "VanillaVersionCatalogService"
        ];
        var fields = typeof(LiveVanillaWizardViewModel)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.FieldType.Name)
            .ToArray();

        Assert.DoesNotContain(fields, name => forbidden.Contains(name, StringComparer.Ordinal));

        var source = File.ReadAllText(Path.Combine(LiveDirectory, "LiveVanillaWizardViewModel.cs"));
        Assert.DoesNotContain("File.WriteAllText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.CreateDirectory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ intent step

    [Fact]
    public void Only_vanilla_is_live_and_every_other_intent_says_so()
    {
        var model = new LiveVanillaWizardViewModel(new FakeGateway());

        Assert.Equal(CreationWizardStep.Intent, model.CurrentStep);
        var live = model.Intents.Where(intent => intent.IsLive).ToArray();
        Assert.Single(live);
        Assert.Equal(CreationIntent.Vanilla, live[0].Intent);
        Assert.All(model.Intents.Where(intent => !intent.IsLive), intent =>
        {
            Assert.False(string.IsNullOrWhiteSpace(intent.Availability));
            Assert.Contains(intent.Availability, intent.AutomationName, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Choosing_an_unavailable_intent_selects_nothing_and_cannot_continue()
    {
        var model = new LiveVanillaWizardViewModel(new FakeGateway());

        model.SelectedIntent = model.Intents.First(intent => intent.Intent == CreationIntent.Modpack);

        Assert.Null(model.SelectedIntent);
        Assert.False(model.NextCommand.CanExecute(null));
        Assert.False(model.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Choosing_vanilla_allows_the_next_step_and_loads_the_real_catalogue()
    {
        var gateway = new FakeGateway();
        var model = Vanilla(gateway);

        Assert.True(model.NextCommand.CanExecute(null));
        await model.NextCommand.ExecuteAsync(null);

        Assert.Equal(CreationWizardStep.Setup, model.CurrentStep);
        Assert.Equal(1, gateway.CatalogRequests);
        Assert.Equal(LiveCatalogState.Available, model.CatalogState);
    }

    // ------------------------------------------------------------------ catalogue states

    [Fact]
    public async Task Stable_releases_are_the_default_and_snapshots_are_an_explicit_choice()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);

        Assert.False(model.IncludeSnapshots);
        Assert.All(model.Versions, version => Assert.Equal(VanillaReleaseChannel.Stable, version.Channel));
        Assert.Contains("releases only", model.ChannelDescription, StringComparison.OrdinalIgnoreCase);

        model.IncludeSnapshots = true;
        await gateway.SettleAsync();
        Assert.Contains(model.Versions, version => version.Channel == VanillaReleaseChannel.Snapshot);
        Assert.True(gateway.LastIncludeSnapshots);
    }

    [Fact]
    public async Task A_snapshot_carries_its_warning_rather_than_being_offered_silently()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);
        model.IncludeSnapshots = true;
        await gateway.SettleAsync();

        var snapshot = model.Versions.First(version => version.Channel == VanillaReleaseChannel.Snapshot);
        model.SelectedVersion = snapshot;

        Assert.True(model.HasWarningMessages);
        Assert.Contains(model.WarningMessages, message =>
            message.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(VanillaVersionSupport.SupportedWithWarning, snapshot.Support);
    }

    [Fact]
    public async Task A_cached_catalogue_says_it_is_cached_and_stays_usable()
    {
        var gateway = new FakeGateway
        {
            Catalog = FakeGateway.SampleCatalog() with { IsFromCache = true, IsStale = false }
        };
        var model = await AtSetupAsync(gateway);

        Assert.Equal(LiveCatalogState.Cached, model.CatalogState);
        Assert.True(model.ShowsCacheNotice);
        Assert.False(model.ShowsCatalogProblem);
        Assert.True(model.HasVersions);
    }

    [Fact]
    public async Task A_stale_catalogue_warns_without_hiding_the_versions_it_still_has()
    {
        var gateway = new FakeGateway
        {
            Catalog = FakeGateway.SampleCatalog() with
            {
                IsFromCache = true,
                IsStale = true,
                UnavailableDetail = "ChunkPilot could not reach Mojang."
            }
        };
        var model = await AtSetupAsync(gateway);

        Assert.Equal(LiveCatalogState.StaleCache, model.CatalogState);
        Assert.True(model.ShowsCatalogWarning);
        Assert.True(model.HasVersions);
        Assert.Contains(model.WarningMessages, message =>
            message.Contains("last saw", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_provider_outage_with_no_cache_offers_nothing_and_explains_why()
    {
        var gateway = new FakeGateway
        {
            Catalog = VanillaVersionCatalog.Unavailable("ChunkPilot could not reach Mojang and has no saved list.")
        };
        var model = await AtSetupAsync(gateway);

        Assert.Equal(LiveCatalogState.NoUsableMetadata, model.CatalogState);
        Assert.True(model.ShowsCatalogProblem);
        Assert.False(model.HasVersions);
        Assert.Contains("saved list", model.CatalogDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_request_is_reported_as_a_failed_request_not_as_an_empty_list()
    {
        var gateway = new FakeGateway { CatalogFailure = new IOException("The background service is not running.") };
        var model = await AtSetupAsync(gateway);

        Assert.Equal(LiveCatalogState.RequestFailed, model.CatalogState);
        Assert.False(model.HasVersions);
        Assert.Contains("background service", model.CatalogDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_version_that_disappears_after_a_refresh_is_cleared_and_explained()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);
        model.SelectedVersion = model.Versions.First(version => version.VersionId == "9.4");

        gateway.Catalog = FakeGateway.SampleCatalog(withoutVersion: "9.4");
        await model.LoadCatalogAsync(true);

        Assert.Null(model.SelectedVersion);
        Assert.True(model.SelectedVersionDisappeared);
        Assert.Contains(model.WarningMessages, message =>
            message.Contains("no longer in Mojang's list", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_version_without_a_server_download_is_offered_to_nobody()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);

        var missing = model.Versions.First(version => version.VersionId == "3.1");
        Assert.Equal(VanillaVersionSupport.NoServerArtifact, missing.Support);
        Assert.False(missing.IsSelectable);
        Assert.Contains("no server download", VanillaSupportPolicy.Describe(missing.Support),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_version_whose_java_requirement_is_unknown_blocks_rather_than_guesses()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);

        var unknown = model.Versions.First(version => version.VersionId == "2.0");
        Assert.Equal(VanillaVersionSupport.JavaRequirementUnknown, unknown.Support);
        model.SelectedVersion = unknown;

        Assert.True(model.HasBlockingMessages);
        Assert.False(model.NextCommand.CanExecute(null));
        Assert.Contains("cannot tell which Java", model.VersionSupportText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_java_requirement_says_where_it_came_from()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);

        model.SelectedVersion = model.Versions.First(version => version.VersionId == "9.4");
        Assert.Contains("Official version metadata", model.VersionJavaText, StringComparison.OrdinalIgnoreCase);

        model.SelectedVersion = model.Versions.First(version => version.VersionId == "8.2");
        Assert.Contains("Derived from the version number", model.VersionJavaText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_managed_java_summary_promises_a_private_copy_and_no_system_change()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);
        model.SelectedVersion = model.Versions.First(version => version.VersionId == "9.4");

        Assert.Contains("Managed Java 25", model.ManagedJavaSummary, StringComparison.Ordinal);
        Assert.Contains("Adoptium", model.ManagedJavaSummary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ name and destination

    [Fact]
    public async Task A_valid_name_resolves_a_managed_destination_the_user_never_typed()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);
        model.ServerName = "Sunday survival";
        await gateway.SettleAsync();

        Assert.Equal("Sunday survival", gateway.LastDestinationName);
        Assert.False(model.HasNameMessages);
        Assert.Contains("Sunday-survival", model.DestinationSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_name_is_refused_before_the_agent_is_asked_anything()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);
        model.ServerName = "CON:my server.";

        Assert.True(model.HasNameMessages);
        Assert.Null(gateway.LastDestinationName);
        Assert.False(model.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_destination_the_policy_refuses_blocks_creation_with_the_policy_wording()
    {
        var gateway = new FakeGateway
        {
            Destination = new VanillaDestinationPreview
            {
                ServerName = "Sunday survival",
                IsAvailable = false,
                Verdict = CreationDestinationVerdict.BlockedNotEmpty,
                Message = "That folder already has files in it. Nothing was changed."
            }
        };
        var model = await AtSetupAsync(gateway);
        await SetNameAsync(model, gateway, "Sunday survival");
        model.SelectedVersion = model.Versions.First(version => version.VersionId == "9.4");

        Assert.True(model.HasLocationMessages);
        Assert.Contains(model.LocationMessages, message =>
            message.Contains("already has files", StringComparison.OrdinalIgnoreCase));
        Assert.False(model.NextCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------ review

    [Fact]
    public async Task The_review_states_the_version_java_destination_and_what_is_not_configured()
    {
        var model = await AtReviewAsync(new FakeGateway());
        var rows = model.Review.Sections.SelectMany(section => section.Rows).ToArray();

        Assert.Contains(rows, row => row.Label == "Minecraft version" && row.Value == "9.4");
        Assert.Contains(rows, row => row.Label == "Java requirement" && row.Value == "Java 25");
        Assert.Contains(rows, row => row.Label == "Release channel" && row.Value == "Release");
        Assert.Contains(rows, row => row.Label == "Destination" &&
                                     row.Value.Contains("Sunday-survival", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Label == "Initial state" && row.Value == "Stopped");
        Assert.Contains(rows, row => row.Label == "Public access" && row.Value == "Not configured");
        Assert.Contains(rows, row => row.Label == "World" && row.Value == "Created on first start");

        // The conversational labels the polish pass removed must not come back.
        Assert.DoesNotContain(rows, row =>
            row.Label is "What you have" or "Java it will use" or "Who can reach it" or "Starting state");
    }

    [Fact]
    public async Task The_review_claims_nothing_that_has_not_happened_yet()
    {
        var model = await AtReviewAsync(new FakeGateway());
        var everything = string.Join(" ", model.Review.Sections
            .SelectMany(section => section.Rows.Select(row => $"{row.Label} {row.Value}")
                .Concat(section.Notes.Select(note => $"{note.Label} {note.Text}")))
            .Concat(model.Review.EvidenceRows.Select(row => $"{row.Label} {row.Value}"))
            .Concat(model.Review.EvidenceNotes.Select(note => note.Text)));

        foreach (var claim in new[]
                 {
                     "已下载", "was downloaded", "has been downloaded", "hash verified", "hash was verified",
                     "Java is installed", "server registered", "is running", "players online"
                 })
            Assert.DoesNotContain(claim, everything, StringComparison.OrdinalIgnoreCase);

        // The future tense is what makes this honest.
        Assert.Contains("is computed after", everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Integrity_evidence_and_provenance_live_behind_progressive_disclosure()
    {
        var model = await AtReviewAsync(new FakeGateway());

        Assert.Contains(model.Review.EvidenceRows, row =>
            row.Label == "Published checksum" && row.Value.StartsWith("SHA-1 ", StringComparison.Ordinal));
        Assert.Contains(model.Review.EvidenceRows, row =>
            row.Label == "Version details" && row.Value.StartsWith("https://", StringComparison.Ordinal));
        Assert.Contains(model.Review.EvidenceRows, row => row.Label == "Metadata freshness");

        // The main sections stay readable: no hash, no URL and no candidate id among them.
        var main = string.Join(" ", model.Review.Sections
            .SelectMany(section => section.Rows.Select(row => row.Value)));
        Assert.DoesNotContain("SHA-1", main, StringComparison.Ordinal);
        Assert.DoesNotContain("piston-", main, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_version_with_no_published_checksum_says_so_rather_than_implying_one()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);
        await SetNameAsync(model, gateway, "Sunday survival");
        model.SelectedVersion = model.Versions.First(version => version.VersionId == "8.2");

        var checksum = model.Review.EvidenceRows.First(row => row.Label == "Published checksum");
        Assert.True(checksum.IsUnknown);
        Assert.Contains("None published", checksum.UnknownText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Going_back_keeps_the_valid_answers_and_drops_the_acceptance()
    {
        var model = await AtReviewAsync(new FakeGateway());
        model.ServerPortText = "25570";
        model.NetworkingPreference = VanillaNetworkingPreference.ThisNetworkOnly;
        model.EulaAccepted = true;
        Assert.True(model.CreateCommand.CanExecute(null));

        model.BackCommand.Execute(null);

        Assert.Equal(CreationWizardStep.Setup, model.CurrentStep);
        Assert.Equal("Sunday survival", model.ServerName);
        Assert.Equal("9.4", model.SelectedVersion?.VersionId);
        Assert.Equal("25570", model.ServerPortText);
        Assert.Equal(VanillaNetworkingPreference.ThisNetworkOnly, model.NetworkingPreference);
        Assert.False(model.EulaAccepted);
    }

    [Fact]
    public async Task Port_and_networking_preference_are_validated_preserved_and_reviewed_without_exposure()
    {
        var model = await AtReviewAsync(new FakeGateway());

        Assert.Equal("25565", model.ServerPortText);
        Assert.Equal(VanillaNetworkingPreference.FriendsOverInternet, model.NetworkingPreference);
        model.ServerPortText = "70000";
        Assert.True(model.HasServerPortInputError);
        Assert.False(model.CreateCommand.CanExecute(null));

        model.ServerPortText = "25570";
        model.NetworkingPreference = VanillaNetworkingPreference.ThisNetworkOnly;
        var plan = model.BuildPlan();
        var access = model.Review.Sections.Single(section => section.Title == "Access").Rows;

        Assert.Equal(25570, plan.Port);
        Assert.Equal(VanillaNetworkingPreference.ThisNetworkOnly, plan.NetworkingPreference);
        Assert.Contains(access, row => row.Label == "Server port" && row.Value == "25570");
        Assert.Contains(access, row => row.Label == "Networking preference" &&
                                       row.Value.Contains("This network only", StringComparison.Ordinal));
        Assert.Contains(access, row => row.Label == "Public access" && row.Value == "Not configured");
        Assert.Contains(access, row => row.Label == "Port availability" &&
                                       row.Value.Contains("when the server starts", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("25565.5")]
    [InlineData("abc")]
    public void Invalid_ports_never_enter_a_ready_creation_plan(string text)
    {
        var parsed = ServerPortPolicy.Parse(text);
        Assert.Null(parsed.Port);
        Assert.NotEmpty(parsed.Error);
    }

    // ------------------------------------------------------------------ EULA

    [Fact]
    public async Task The_acceptance_control_starts_unchecked_in_every_fresh_session()
    {
        Assert.False(new LiveVanillaWizardViewModel(new FakeGateway()).EulaAccepted);
        Assert.False((await AtReviewAsync(new FakeGateway())).EulaAccepted);
    }

    [Fact]
    public async Task Creation_is_refused_until_the_eula_is_accepted()
    {
        var model = await AtReviewAsync(new FakeGateway());

        Assert.False(model.CreateCommand.CanExecute(null));
        model.EulaAccepted = true;
        Assert.True(model.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Opening_the_official_eula_shows_it_and_accepts_nothing()
    {
        var opener = new RecordingLinkOpener();
        var model = await AtReviewAsync(new FakeGateway(), opener);

        model.OpenEulaCommand.Execute(null);

        Assert.Equal([VanillaEulaAcceptance.OfficialSourceUrl], opener.Opened);
        Assert.False(model.EulaAccepted);
        Assert.False(model.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Choosing_a_version_or_moving_between_steps_never_accepts_the_eula()
    {
        var gateway = new FakeGateway();
        var model = await AtSetupAsync(gateway);
        await SetNameAsync(model, gateway, "Sunday survival");
        model.SelectedVersion = model.Versions.First(version => version.VersionId == "9.4");
        Assert.False(model.EulaAccepted);

        model.NextCommand.Execute(null);
        Assert.Equal(CreationWizardStep.Review, model.CurrentStep);
        Assert.False(model.EulaAccepted);
    }

    [Fact]
    public async Task Acceptance_records_the_moment_and_the_official_source_and_no_legal_text()
    {
        var model = await AtReviewAsync(new FakeGateway());
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        model.EulaAccepted = true;
        var acceptance = model.Eula;

        Assert.True(acceptance.IsAuthorised);
        Assert.NotNull(acceptance.AcceptedAtUtc);
        Assert.InRange(acceptance.AcceptedAtUtc!.Value, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(VanillaEulaAcceptance.OfficialSourceUrl, acceptance.SourceUrl);
        Assert.DoesNotContain("MINECRAFT END USER LICENCE", model.EulaAcceptedDetail, StringComparison.OrdinalIgnoreCase);
        Assert.True(model.EulaAcceptedDetail.Length < 400);
    }

    [Fact]
    public async Task Changing_what_will_be_built_withdraws_the_acceptance()
    {
        var model = await AtReviewAsync(new FakeGateway());
        model.EulaAccepted = true;

        model.SelectedVersion = model.Versions.First(version => version.VersionId == "8.2");
        Assert.False(model.EulaAccepted);
        Assert.Null(model.Eula.AcceptedAtUtc);

        model.EulaAccepted = true;
        model.ServerName = "Something else";
        Assert.False(model.EulaAccepted);
    }

    [Fact]
    public async Task An_unaccepted_plan_is_refused_by_the_plan_contract_itself()
    {
        var model = await AtReviewAsync(new FakeGateway());

        var plan = model.BuildPlan();
        Assert.False(plan.IsReady);
        Assert.Contains(plan.Problems(), problem =>
            problem.Contains("EULA was not accepted", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ submission

    [Fact]
    public async Task Creating_submits_exactly_one_plan_carrying_the_exact_version_and_acceptance()
    {
        var gateway = new FakeGateway();
        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;

        await model.CreateCommand.ExecuteAsync(null);

        var plan = Assert.Single(gateway.Submitted);
        Assert.Equal("Sunday survival", plan.ServerName);
        Assert.Equal("9.4", plan.Version.VersionId);
        Assert.Equal(25, plan.Version.RequiredJavaMajor);
        Assert.Equal(JavaRequirementSource.OfficialMetadata, plan.Version.JavaRequirementSource);
        Assert.Equal(FakeGateway.ReleaseSha1, plan.Version.ServerSha1);
        Assert.True(plan.Eula.IsAuthorised);
        Assert.True(plan.IsReady);
    }

    [Fact]
    public async Task Clicking_create_twice_cannot_start_two_operations()
    {
        var gateway = new FakeGateway { HoldSubmission = true };
        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;

        var first = model.CreateCommand.ExecuteAsync(null);
        // The guard is set before the first await, so the second click cannot get past it even while
        // the first submission is still in flight.
        Assert.False(model.CreateCommand.CanExecute(null));
        await model.CreateCommand.ExecuteAsync(null);
        gateway.ReleaseSubmission();
        await first;

        Assert.Single(gateway.Submitted);
        Assert.Single(gateway.Submitted.Select(plan => plan.OperationId).Distinct());
    }

    [Fact]
    public async Task A_refused_submission_is_reported_without_claiming_anything_was_changed()
    {
        var gateway = new FakeGateway
        {
            BeginFailure = new InvalidOperationException("This creation has already been started.")
        };
        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;

        await model.CreateCommand.ExecuteAsync(null);

        Assert.Equal(CreationWizardStep.Completion, model.CurrentStep);
        Assert.Equal(CreationStage.FailedNothingChanged, model.OperationStage);
        Assert.Contains("already been started", model.OutcomeMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ progress and cancellation

    [Fact]
    public async Task A_download_with_a_known_total_is_the_only_determinate_progress()
    {
        var gateway = new FakeGateway();
        gateway.Timeline.Add(Running(CreationStage.PreparingJava, "Getting a private Java 25 runtime"));
        gateway.Timeline.Add(Running(CreationStage.DownloadingServer, "Downloading the server", 40, 12_000_000, 48_000_000));
        gateway.Timeline.Add(Completed());

        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;
        var stages = new List<(CreationStage Stage, bool Indeterminate)>();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.OperationHeadline))
                stages.Add((model.OperationStage, model.IsIndeterminate));
        };

        await model.CreateCommand.ExecuteAsync(null);

        Assert.Contains(stages, entry => entry.Stage == CreationStage.PreparingJava && entry.Indeterminate);
        Assert.Contains(stages, entry => entry.Stage == CreationStage.DownloadingServer && !entry.Indeterminate);
    }

    [Fact]
    public void Every_stage_the_user_can_see_has_wording_that_is_not_an_enum_name()
    {
        foreach (var stage in Enum.GetValues<CreationStage>())
        {
            var text = CreationStagePolicy.Describe(stage);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(stage.ToString(), text);
            // Ordinary prose: a leading capital and separated words. "Preparing Java" legitimately
            // matches its identifier once the space is removed, which is why only the raw identifier
            // itself is forbidden rather than anything resembling it.
            Assert.True(char.IsUpper(text[0]), $"\"{text}\" does not read as a sentence.");
            Assert.DoesNotMatch("[a-z][A-Z][a-z]*[A-Z]", text);
        }
    }

    [Fact]
    public void Successful_creation_outcomes_cannot_regress_to_not_started_at_one_hundred_percent()
    {
        Assert.Equal(CreationStage.Completed,
            CreationStagePolicy.ForSuccessfulOutcome(CreationOutcome.Completed));
        Assert.Equal(CreationStage.CompletedWithCleanupWarning,
            CreationStagePolicy.ForSuccessfulOutcome(CreationOutcome.CompletedWithCleanupWarning));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreationStagePolicy.ForSuccessfulOutcome(CreationOutcome.NothingActivated));
    }

    [Fact]
    public async Task Stopping_before_the_critical_section_says_the_folder_is_untouched()
    {
        var gateway = new FakeGateway { HoldTimeline = true };
        gateway.Timeline.Add(Running(CreationStage.DownloadingServer, "Downloading the server", 40, 1, 100));
        gateway.Timeline.Add(Cancelled());

        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;
        var run = model.CreateCommand.ExecuteAsync(null);
        await gateway.WaitForPollAsync();

        Assert.True(model.StopCommand.CanExecute(null));
        await model.StopCommand.ExecuteAsync(null);

        Assert.Equal(CreationStage.CancellingSafely, model.EffectiveStage);
        Assert.Contains("Nothing has been put in place", model.CancellationNotice, StringComparison.Ordinal);
        Assert.Single(gateway.Cancellations);

        gateway.Release();
        await run;
        Assert.Equal(CreationStage.Cancelled, model.OperationStage);
    }

    [Fact]
    public async Task Stopping_inside_the_critical_section_promises_a_safe_checkpoint_not_an_instant_stop()
    {
        var gateway = new FakeGateway { HoldTimeline = true };
        gateway.Timeline.Add(Running(CreationStage.Registering, "Adding the server to ChunkPilot", 96));
        gateway.Timeline.Add(Completed());

        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;
        var run = model.CreateCommand.ExecuteAsync(null);
        await gateway.WaitForPollAsync();

        await model.StopCommand.ExecuteAsync(null);

        Assert.Equal(CreationStage.WaitingForSafeCheckpoint, model.EffectiveStage);
        Assert.Contains("finish this step", model.CancellationNotice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing has been put in place", model.CancellationNotice, StringComparison.Ordinal);

        gateway.Release();
        await run;
        // Cancelling after the point of no return does not undo a server that was created.
        Assert.True(model.IsSuccessful);
    }

    [Fact]
    public async Task Asking_to_stop_twice_is_the_same_as_asking_once()
    {
        var gateway = new FakeGateway { HoldTimeline = true };
        gateway.Timeline.Add(Running(CreationStage.DownloadingServer, "Downloading the server", 40, 1, 100));
        gateway.Timeline.Add(Cancelled());

        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;
        var run = model.CreateCommand.ExecuteAsync(null);
        await gateway.WaitForPollAsync();

        await model.StopCommand.ExecuteAsync(null);
        Assert.False(model.StopCommand.CanExecute(null));
        await model.StopCommand.ExecuteAsync(null);

        Assert.Single(gateway.Cancellations);
        gateway.Release();
        await run;
    }

    // ------------------------------------------------------------------ outcomes

    [Fact]
    public async Task A_completed_creation_reports_the_server_the_version_and_the_runtime_it_was_given()
    {
        var gateway = new FakeGateway();
        gateway.Timeline.Add(Completed());
        var model = await AtReviewAsync(gateway, navigator: new RecordingNavigator());
        model.EulaAccepted = true;

        await model.CreateCommand.ExecuteAsync(null);

        Assert.True(model.IsSuccessful);
        Assert.Equal(CreationWizardStep.Completion, model.CurrentStep);
        Assert.Equal("Sunday survival", model.CreatedServerName);
        Assert.Equal("9.4", model.CreatedServerVersion);
        Assert.Equal("Managed Java 25", model.CreatedJavaSummary);
        Assert.Contains("Eclipse Adoptium", model.CreatedJavaDetails, StringComparison.Ordinal);
        Assert.Contains("Java 25", model.CreatedJavaDetails, StringComparison.Ordinal);
        Assert.True(model.ShowsOpenServer);
    }

    [Fact]
    public async Task A_cleanup_warning_is_still_a_created_server_and_says_what_remains()
    {
        var gateway = new FakeGateway();
        gateway.Timeline.Add(Completed(CreationOutcome.CompletedWithCleanupWarning,
            CreationStage.CompletedWithCleanupWarning,
            ["The temporary working folder could not be removed: it is in use."]));
        var model = await AtReviewAsync(gateway, navigator: new RecordingNavigator());
        model.EulaAccepted = true;

        await model.CreateCommand.ExecuteAsync(null);

        Assert.True(model.IsSuccessful);
        Assert.False(model.NeedsAttention);
        Assert.True(model.ShowsOpenServer);
        Assert.Contains(model.OutcomeWarnings, warning =>
            warning.Contains("temporary working folder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_rollback_is_reported_as_a_rollback_rather_than_as_a_bare_failure()
    {
        var gateway = new FakeGateway();
        gateway.Timeline.Add(Terminal(CreationStage.FailedRolledBack, CreationOutcome.RolledBack,
            "The server could not be added to ChunkPilot, so the change was undone."));
        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;

        await model.CreateCommand.ExecuteAsync(null);

        Assert.Equal(CreationStage.FailedRolledBack, model.OperationStage);
        Assert.True(model.IsFailed);
        Assert.False(model.NeedsAttention);
        Assert.False(model.ShowsOpenServer);
        Assert.Contains("undone", model.OperationHeadline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_recovery_required_outcome_never_calls_the_server_ready_and_offers_only_safe_actions()
    {
        var gateway = new FakeGateway();
        gateway.Timeline.Add(Terminal(CreationStage.RecoveryRequired, CreationOutcome.RecoveryRequired,
            "The files are in place but the server was not fully added to ChunkPilot."));
        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;

        await model.CreateCommand.ExecuteAsync(null);

        Assert.True(model.NeedsAttention);
        Assert.False(model.IsSuccessful);
        Assert.False(model.ShowsOpenServer);
        Assert.Contains("protect your files", model.OperationHeadline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(model.OperationIdText, model.DiagnosticDetails, StringComparison.Ordinal);

        var window = File.ReadAllText(WindowXamlFile);
        foreach (var forbidden in new[] { "Delete", "Overwrite", "Force complete", "Take ownership", "Ignore and continue" })
            Assert.DoesNotContain($"Content=\"{forbidden}", window, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Losing_contact_with_the_agent_is_not_reported_as_the_work_having_stopped()
    {
        var gateway = new FakeGateway { SnapshotFailure = new IOException("The pipe was closed.") };
        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;

        await model.CreateCommand.ExecuteAsync(null);

        Assert.Equal(CreationStage.RecoveryRequired, model.OperationStage);
        Assert.Contains("may still be going on", model.OutcomeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("", model.OperationIdText);
    }

    [Fact]
    public async Task Opening_the_created_server_uses_the_shell_navigation_and_creates_nothing()
    {
        var gateway = new FakeGateway();
        gateway.Timeline.Add(Completed());
        var navigator = new RecordingNavigator();
        var model = await AtReviewAsync(gateway, navigator: navigator);
        model.EulaAccepted = true;
        await model.CreateCommand.ExecuteAsync(null);

        var closed = false;
        model.CloseRequested += (_, _) => closed = true;
        await model.OpenServerCommand.ExecuteAsync(null);

        Assert.Equal([FakeGateway.CreatedServerId], navigator.Opened);
        Assert.True(closed);
        Assert.Single(gateway.Submitted);
    }

    [Fact]
    public async Task Closing_the_window_stops_watching_and_never_cancels_the_operation()
    {
        var gateway = new FakeGateway { HoldTimeline = true };
        gateway.Timeline.Add(Running(CreationStage.DownloadingServer, "Downloading the server", 40, 1, 100));
        gateway.Timeline.Add(Completed());

        var model = await AtReviewAsync(gateway);
        model.EulaAccepted = true;
        var run = model.CreateCommand.ExecuteAsync(null);
        await gateway.WaitForPollAsync();

        model.Dispose();
        gateway.Release();
        await run;

        Assert.Empty(gateway.Cancellations);
    }

    [Fact]
    public async Task Reopening_reattaches_to_work_the_agent_is_still_doing()
    {
        var gateway = new FakeGateway { HoldTimeline = true };
        gateway.Existing.Add(Running(CreationStage.DownloadingServer, "Downloading the server", 40, 5, 100));
        gateway.Timeline.Add(Running(CreationStage.DownloadingServer, "Downloading the server", 45, 6, 100));

        var model = new LiveVanillaWizardViewModel(gateway, pollInterval: TimeSpan.FromMilliseconds(1));
        var reattached = await model.TryReattachAsync();

        Assert.True(reattached);
        Assert.Equal(CreationWizardStep.Creating, model.CurrentStep);
        Assert.Equal(CreationStage.DownloadingServer, model.OperationStage);
        Assert.False(model.CreateCommand.CanExecute(null));
        Assert.Empty(gateway.Submitted);
        model.Dispose();
        gateway.Release();
    }

    [Fact]
    public async Task Reopening_with_nothing_running_starts_at_the_first_step()
    {
        var gateway = new FakeGateway();
        var model = new LiveVanillaWizardViewModel(gateway, pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.False(await model.TryReattachAsync());
        Assert.Equal(CreationWizardStep.Intent, model.CurrentStep);
    }

    // ------------------------------------------------------------------ visual and accessibility contract

    [Fact]
    public void The_live_window_loads_with_the_real_design_system_and_every_resource_resolves()
    {
        var realised = WpfDesignSystemHost.Run(() =>
        {
            var window = new CreateServerLiveWindow(new LiveVanillaWizardViewModel(new FakeGateway()));
            try
            {
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1440, 900));
                content.Arrange(new Rect(0, 0, 1440, 900));
                content.UpdateLayout();
                return (window.Title, Buttons: Count<Button>(content), Lists: Count<ListBox>(content));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Contains("Create a server", realised.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preview", realised.Title, StringComparison.OrdinalIgnoreCase);
        Assert.True(realised.Buttons > 0, "The live window realised no buttons at all.");
        Assert.True(realised.Lists > 0, "The live window realised no lists at all.");
    }

    [Fact]
    public void Every_interactive_element_in_the_live_window_is_named_for_assistive_technology()
    {
        var root = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(root);

        string[] interactive = ["Button", "TextBox", "CheckBox", "ListBox"];
        var unnamed = root.DescendantsAndSelf()
            .Where(element => interactive.Contains(element.Name.LocalName, StringComparer.Ordinal))
            .Where(element => element.Attribute(DesignSystemFiles.XamlNamespace + "Key") is null)
            .Where(element => !element.Attributes().Any(attribute =>
                attribute.Name.ToString().Contains("AutomationProperties.Name", StringComparison.Ordinal) ||
                attribute.Name.LocalName == "AutomationProperties.Name"))
            .Select(element => element.Name.LocalName)
            .ToArray();

        Assert.True(unnamed.Length == 0, "Unnamed interactive elements: " + string.Join(", ", unnamed));
    }

    [Fact]
    public void Display_components_in_the_live_window_are_not_keyboard_tab_stops()
    {
        string[] displayComponents =
        [
            "AppPageHeader", "AppStatusBadge", "AppAlert", "AppSectionCard",
            "AppEmptyState", "AppInfoRow", "AppServerRow", "AppLoadingState", "AppProgressPanel"
        ];

        var root = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(root);
        var unfocusable = root.Descendants()
            .Where(element => element.Name.LocalName == "Style")
            .Where(element => element.Elements().Any(setter =>
                setter.Name.LocalName == "Setter" &&
                setter.Attribute("Property")?.Value == "Focusable" &&
                setter.Attribute("Value")?.Value == "False"))
            .Select(element => element.Attribute("TargetType")?.Value ?? "")
            .ToArray();

        var missing = displayComponents
            .Where(component => !unfocusable.Any(target => target.Contains(component, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(missing.Length == 0, "Display components still take focus: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_live_window_declares_no_visual_value_and_no_invalid_icon()
    {
        var text = File.ReadAllText(WindowXamlFile);
        var root = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(root);

        Assert.DoesNotMatch("#[0-9A-Fa-f]{3,8}", text);
        var literals = root.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "FontFamily" or "FontSize" or "Foreground" or "Background")
            .Where(attribute => !attribute.Value.StartsWith('{'))
            .Select(attribute => $"{attribute.Name.LocalName}=\"{attribute.Value}\"")
            .ToArray();
        Assert.True(literals.Length == 0, "Literal visual values: " + string.Join(", ", literals));

        var names = Enum.GetNames<AppIconKind>().ToHashSet(StringComparer.Ordinal);
        var icons = root.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "Kind" or "Icon" ||
                                attribute.Name.ToString().EndsWith("AppButton.Icon", StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .Where(value => !value.StartsWith('{'))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(icons);
        var invalid = icons.Where(value => !names.Contains(value)).ToArray();
        Assert.True(invalid.Length == 0, "Unknown icon names: " + string.Join(", ", invalid));
    }

    [Fact]
    public void The_live_window_owns_exactly_one_scroll_region_and_no_tab_navigation()
    {
        var root = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(root);

        Assert.Equal(1, root.DescendantsAndSelf().Count(element => element.Name.LocalName == "ScrollViewer"));
        Assert.DoesNotContain(root.DescendantsAndSelf(), element =>
            element.Name.LocalName is "TabControl" or "TabItem");
    }

    [Fact]
    public void The_live_window_uses_the_shared_button_vocabulary_only()
    {
        var root = XDocument.Load(WindowXamlFile).Root;
        Assert.NotNull(root);

        var offenders = root.DescendantsAndSelf()
            .Where(element => element.Name.LocalName == "Button")
            .Select(element => element.Attribute("Style")?.Value)
            .Where(style => style is not null && !style.Contains("StaticResource App", StringComparison.Ordinal))
            .ToArray();

        Assert.True(offenders.Length == 0, "Buttons with a non-design-system style: " + string.Join(", ", offenders!));
    }

    [Fact]
    public void The_setup_step_exposes_no_technical_field_a_beginner_should_never_see()
    {
        var window = File.ReadAllText(WindowXamlFile);

        foreach (var forbidden in new[]
                 {
                     "JavaPath", "JarPath", "LaunchArguments", "ServerDirectory", "InstanceRoot",
                     "Port", "MinimumRamMb", "MaximumRamMb", "server.properties"
                 })
            Assert.DoesNotContain($"Binding {forbidden}", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Browse", window, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_window_says_it_is_a_development_build_and_that_it_creates_something_real()
    {
        var window = File.ReadAllText(WindowXamlFile);

        // The badge in the header carries this once. It used to be repeated in the footer of every
        // step, which is prose the wizard does not need.
        // Stated as icon and text since the polish pass; the capsule around it was decoration.
        Assert.Contains("Development build · Vanilla only", window, StringComparison.Ordinal);
        Assert.DoesNotContain("This wizard creates a real server", window, StringComparison.Ordinal);
        Assert.Contains("Create server", window, StringComparison.Ordinal);
        // The preview's unmissable "nothing is installed" wording must not appear here: it would be
        // false, and the two windows must never be mistaken for each other.
        Assert.DoesNotContain("Design preview", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing is installed", window, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static LiveVanillaWizardViewModel Vanilla(
        FakeGateway gateway,
        ISafeLinkOpener? links = null,
        ICreatedServerNavigator? navigator = null)
    {
        var model = new LiveVanillaWizardViewModel(gateway, links, navigator, TimeSpan.FromMilliseconds(1));
        model.SelectedIntent = model.Intents.First(intent => intent.Intent == CreationIntent.Vanilla);
        return model;
    }

    /// <summary>
    /// Drives the wizard to the setup step and waits for the catalogue the real one would load.
    /// </summary>
    /// <remarks>
    /// Every step transition here goes through the real commands rather than by setting the step, so
    /// a test cannot reach a state the interface itself cannot reach.
    /// </remarks>
    private static async Task<LiveVanillaWizardViewModel> AtSetupAsync(
        FakeGateway gateway,
        ISafeLinkOpener? links = null,
        ICreatedServerNavigator? navigator = null)
    {
        var model = Vanilla(gateway, links, navigator);
        await model.NextCommand.ExecuteAsync(null);
        return model;
    }

    private static async Task<LiveVanillaWizardViewModel> AtReviewAsync(
        FakeGateway gateway,
        ISafeLinkOpener? links = null,
        ICreatedServerNavigator? navigator = null)
    {
        var model = await AtSetupAsync(gateway, links, navigator);
        await SetNameAsync(model, gateway, "Sunday survival");
        model.SelectedVersion = model.Versions.First(version => version.VersionId == "9.4");
        await model.NextCommand.ExecuteAsync(null);
        return model;
    }

    /// <summary>Sets the name and waits for the destination answer the real Agent would send back.</summary>
    private static async Task SetNameAsync(LiveVanillaWizardViewModel model, FakeGateway gateway, string name)
    {
        model.ServerName = name;
        for (var attempt = 0; attempt < 2_000 && !model.HasResolvedDestination; attempt++)
            await Task.Delay(1);
        Assert.True(model.HasResolvedDestination,
            $"The destination preview for '{name}' did not settle before the test deadline.");
    }

    private static InstallOperationSnapshot Running(
        CreationStage stage, string step, double percent = 10, long bytes = 0, long? total = null) =>
        new()
        {
            OperationId = FakeGateway.OperationId,
            Progress = new InstallProgress
            {
                OperationId = FakeGateway.OperationId,
                Stage = stage,
                CurrentStep = step,
                OverallPercent = percent,
                BytesDownloaded = bytes,
                TotalBytes = total
            },
            Outcome = CreationOutcome.InProgress
        };

    private static InstallOperationSnapshot Completed(
        CreationOutcome outcome = CreationOutcome.Completed,
        CreationStage stage = CreationStage.Completed,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            OperationId = FakeGateway.OperationId,
            Progress = new InstallProgress
            {
                OperationId = FakeGateway.OperationId,
                Stage = stage,
                CurrentStep = CreationStagePolicy.Describe(stage),
                OverallPercent = 100
            },
            IsTerminal = true,
            Success = true,
            Outcome = outcome,
            Warnings = warnings ?? [],
            Result = new InstallationResult
            {
                Definition = new ServerDefinition
                {
                    Id = FakeGateway.CreatedServerId,
                    Name = "Sunday survival",
                    MinecraftVersion = "9.4",
                    RootPath = FakeGateway.CreatedPath,
                    Executable = FakeGateway.JavaPath,
                    IsManaged = true,
                    Ecosystem = ServerEcosystem.Vanilla
                },
                Outcome = outcome,
                Warnings = warnings ?? []
            }
        };

    private static InstallOperationSnapshot Cancelled() =>
        Terminal(CreationStage.Cancelled, CreationOutcome.NothingActivated,
            "Creation stopped. Nothing was put in place.");

    private static InstallOperationSnapshot Terminal(
        CreationStage stage, CreationOutcome outcome, string error) =>
        new()
        {
            OperationId = FakeGateway.OperationId,
            Progress = new InstallProgress
            {
                OperationId = FakeGateway.OperationId,
                Stage = stage,
                CurrentStep = CreationStagePolicy.Describe(stage)
            },
            IsTerminal = true,
            Success = false,
            Error = error,
            Outcome = outcome
        };

    private static int Count<T>(DependencyObject root) where T : DependencyObject
    {
        var found = root is T ? 1 : 0;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            found += Count<T>(VisualTreeHelper.GetChild(root, index));
        return found;
    }

    private sealed class RecordingLinkOpener : ISafeLinkOpener
    {
        public List<string> Opened { get; } = [];

        public void Open(string url) => Opened.Add(url);
    }

    private sealed class RecordingNavigator : ICreatedServerNavigator
    {
        public List<Guid> Opened { get; } = [];

        public Task OpenAsync(Guid serverId)
        {
            Opened.Add(serverId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A complete Agent stand-in: a fixed catalogue, a scripted operation timeline, and a record of
    /// everything that was asked of it.
    /// </summary>
    private sealed class FakeGateway : IVanillaCreationGateway
    {
        public const string ReleaseSha1 = "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b";
        public const string CreatedPath = @"C:\Users\Test\ChunkPilot\Servers\Sunday-survival";
        public const string JavaPath = @"C:\Users\Test\AppData\Local\ChunkPilot\ManagedJava\temurin-25\bin\java.exe";

        public static readonly Guid OperationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        public static readonly Guid CreatedServerId = Guid.Parse("99999999-8888-7777-6666-555555555555");

        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource submission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int polls;

        public VanillaVersionCatalog Catalog { get; set; } = SampleCatalog();

        public Exception? CatalogFailure { get; set; }

        public Exception? BeginFailure { get; set; }

        public Exception? SnapshotFailure { get; set; }

        public VanillaDestinationPreview? Destination { get; set; }

        public bool HoldTimeline { get; set; }

        public bool HoldSubmission { get; set; }

        public List<InstallOperationSnapshot> Timeline { get; } = [];

        public List<InstallOperationSnapshot> Existing { get; } = [];

        public ConcurrentBag<VanillaCreationPlan> Submitted { get; } = [];

        public ConcurrentBag<Guid> Cancellations { get; } = [];

        public int CatalogRequests { get; private set; }

        public bool LastIncludeSnapshots { get; private set; }

        public const string DefaultRoot = @"C:\Users\Test\ChunkPilot\Servers";

        public string? LastDestinationName { get; private set; }

        public string LastInstanceRoot { get; private set; } = "";

        public int DestinationRequests { get; private set; }

        public void Release() => release.TrySetResult();

        /// <summary>
        /// Lets a fire-and-forget request the view model started finish before the test asserts.
        /// </summary>
        /// <remarks>
        /// The destination lookup is deliberately not awaited by the view model — typing a name must
        /// not block the interface — so a test has to give the continuation a turn. Yielding twice is
        /// enough for a gateway whose only asynchrony is <c>Task.Yield</c>.
        /// </remarks>
        public async Task SettleAsync()
        {
            await Task.Yield();
            await Task.Yield();
            await Task.Yield();
        }

        public void ReleaseSubmission() => submission.TrySetResult();

        /// <summary>Completes once the first snapshot has been applied to the view model.</summary>
        public Task WaitForPollAsync() => applied.Task;

        public async Task<VanillaVersionCatalog> GetCatalogAsync(
            bool includeSnapshots, bool forceRefresh, CancellationToken cancellationToken)
        {
            CatalogRequests++;
            LastIncludeSnapshots = includeSnapshots;
            await Task.Yield();
            if (CatalogFailure is not null)
                throw CatalogFailure;
            return Catalog;
        }

        public async Task<VanillaDestinationPreview> PreviewDestinationAsync(
            string serverName, string instanceRoot, CancellationToken cancellationToken)
        {
            LastDestinationName = serverName;
            LastInstanceRoot = instanceRoot;
            DestinationRequests++;
            await Task.Yield();
            var root = instanceRoot.Length > 0 ? instanceRoot : DefaultRoot;
            return Destination ?? new VanillaDestinationPreview
            {
                ServerName = serverName,
                FolderName = serverName.Replace(' ', '-'),
                InstanceRoot = root,
                CanonicalDestination = Path.Combine(root, serverName.Replace(' ', '-')),
                Verdict = CreationDestinationVerdict.Available,
                IsAvailable = true,
                Message = ""
            };
        }

        public async Task<Guid> BeginAsync(VanillaCreationPlan plan, CancellationToken cancellationToken)
        {
            if (HoldSubmission)
                await submission.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await Task.Yield();
            if (BeginFailure is not null)
                throw BeginFailure;
            Submitted.Add(plan);
            return OperationId;
        }

        public async Task<InstallOperationSnapshot> GetSnapshotAsync(
            Guid operationId, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (SnapshotFailure is not null)
                throw SnapshotFailure;
            if (Timeline.Count == 0)
                return Completed();
            var index = Math.Min(polls, Timeline.Count - 1);
            if (polls > 0)
            {
                // The first entry has been applied by now, so a test can assert against it. Holding
                // from here lets a cancellation be issued while a chosen stage is genuinely current
                // rather than racing the timeline.
                applied.TrySetResult();
                if (HoldTimeline)
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            polls++;
            return Timeline[index];
        }

        public Task CancelAsync(Guid operationId, CancellationToken cancellationToken)
        {
            Cancellations.Add(operationId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<InstallOperationSnapshot>> GetCreationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstallOperationSnapshot>>(Existing);

        public Task<IReadOnlyList<ManagedJavaRuntime>> GetManagedRuntimesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ManagedJavaRuntime>>(
            [
                new ManagedJavaRuntime
                {
                    Vendor = "Eclipse Adoptium",
                    Version = "jdk-25.0.1+9",
                    MajorVersion = 25,
                    Architecture = "x64",
                    JavaPath = JavaPath,
                    IsManaged = true
                }
            ]);

        /// <summary>
        /// A version list with one of every state the wizard has to explain.
        /// </summary>
        public static VanillaVersionCatalog SampleCatalog(string? withoutVersion = null)
        {
            VanillaVersionOption[] options =
            [
                Option("9.4", VanillaReleaseChannel.Stable, true, 25, JavaRequirementSource.OfficialMetadata, ReleaseSha1),
                Option("9.3", VanillaReleaseChannel.Stable, true, 21, JavaRequirementSource.OfficialMetadata, ReleaseSha1),
                Option("8.2", VanillaReleaseChannel.Stable, true, 17, JavaRequirementSource.ChunkPilotPolicy, ""),
                Option("3.1", VanillaReleaseChannel.Stable, false, 8, JavaRequirementSource.ChunkPilotPolicy, ""),
                Option("2.0", VanillaReleaseChannel.Stable, true, null, JavaRequirementSource.Unknown, ""),
                Option("9.5-pre1", VanillaReleaseChannel.Snapshot, true, 25, JavaRequirementSource.OfficialMetadata, ReleaseSha1)
            ];

            return new VanillaVersionCatalog
            {
                Options = options
                    .Where(option => option.VersionId != withoutVersion)
                    .ToArray(),
                RetrievedUtc = new DateTimeOffset(2026, 7, 28, 4, 42, 36, TimeSpan.Zero),
                ProviderAvailable = true
            };
        }

        private static VanillaVersionOption Option(
            string id,
            VanillaReleaseChannel channel,
            bool hasServer,
            int? java,
            JavaRequirementSource source,
            string sha1)
        {
            var warnings = new List<string>();
            if (channel == VanillaReleaseChannel.Snapshot)
                warnings.Add("This is an in-development snapshot. Worlds made on it may not open in a later release.");
            if (source == JavaRequirementSource.ChunkPilotPolicy)
                warnings.Add("Mojang's metadata for this version does not state a Java version.");
            if (hasServer && sha1.Length == 0)
                warnings.Add("Mojang published no checksum for this server download, so it cannot be verified.");

            return new VanillaVersionOption
            {
                VersionId = id,
                Channel = channel,
                ReleaseType = channel == VanillaReleaseChannel.Stable ? "release" : "snapshot",
                ReleaseTime = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                MetadataUrl = $"https://piston-meta.mojang.com/v1/packages/{id}.json",
                HasServerDownload = hasServer,
                ServerDownloadUrl = hasServer ? $"https://piston-data.mojang.com/v1/objects/{id}/server.jar" : "",
                ServerSha1 = sha1,
                ServerSizeBytes = hasServer ? 58_000_000 : null,
                RequiredJavaMajor = java,
                JavaRequirementSource = source,
                Support = VanillaSupportPolicy.Conclude(channel, hasServer, java),
                Provenance = "Official Mojang version metadata",
                Warnings = warnings
            };
        }
    }
}
