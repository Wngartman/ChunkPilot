using ChunkPilot.App.CreateServer;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

/// <summary>
/// Behaviour of the Create Server v2 preview: step sequence, validation, compatibility wording and
/// the review screen.
/// </summary>
/// <remarks>
/// Every test drives the real view model against the real synthetic catalogue. Nothing here needs a
/// dispatcher, a window, a temporary directory or a fake agent, which is itself the point: the
/// preview has no dependency that could reach a provider, the installer or the database.
/// </remarks>
public sealed class CreateServerV2WizardTests
{
    // ------------------------------------------------------------------ initial state

    [Fact]
    public void Initial_state_starts_on_the_intent_step_with_nothing_chosen()
    {
        var model = new CreateServerPreviewViewModel();

        Assert.Equal(CreationWizardStep.Intent, model.CurrentStep);
        Assert.Equal(1, model.CurrentStepNumber);
        Assert.Equal("Step 1 of 4", model.StepPosition);
        Assert.Null(model.SelectedIntent);
        Assert.Null(model.SelectedOption);
        Assert.Equal("", model.ServerName);
        Assert.False(model.IsCompleted);
        Assert.False(model.ShowsBack);
        Assert.True(model.ShowsCancel);
        Assert.True(model.ShowsNext);
        Assert.False(model.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Exactly_the_six_documented_intents_are_offered()
    {
        var model = new CreateServerPreviewViewModel();

        Assert.Equal(
            [
                CreationIntent.Vanilla, CreationIntent.Plugins, CreationIntent.Mods,
                CreationIntent.Modpack, CreationIntent.Crossplay, CreationIntent.Advanced
            ],
            model.Intents.Select(card => card.Intent).ToArray());
    }

    [Fact]
    public void Every_intent_card_carries_title_description_help_and_a_truthful_preview_state()
    {
        foreach (var card in CreationIntentCatalog.Cards)
        {
            Assert.False(string.IsNullOrWhiteSpace(card.Title), $"{card.Intent} has no title");
            Assert.False(string.IsNullOrWhiteSpace(card.Description), $"{card.Intent} has no description");
            Assert.False(string.IsNullOrWhiteSpace(card.HelpText), $"{card.Intent} has no help text");
            Assert.False(string.IsNullOrWhiteSpace(card.PreviewAvailability), $"{card.Intent} claims nothing about the preview");
            Assert.Contains(card.Title, card.AutomationName, StringComparison.Ordinal);
            Assert.True(Enum.IsDefined(card.Icon), $"{card.Intent} uses an undefined icon");
        }
    }

    [Fact]
    public void The_advanced_intent_says_it_is_not_fully_previewable()
    {
        Assert.False(CreationIntentCatalog.For(CreationIntent.Advanced).IsFullyPreviewable);
        Assert.All(
            CreationIntentCatalog.Cards.Where(card => card.Intent != CreationIntent.Advanced),
            card => Assert.True(card.IsFullyPreviewable));
    }

    // ------------------------------------------------------------------ navigation

    [Theory]
    [InlineData(CreationIntent.Vanilla)]
    [InlineData(CreationIntent.Plugins)]
    [InlineData(CreationIntent.Mods)]
    [InlineData(CreationIntent.Modpack)]
    [InlineData(CreationIntent.Crossplay)]
    [InlineData(CreationIntent.Advanced)]
    public void Choosing_any_intent_allows_the_first_step_to_be_left(CreationIntent intent)
    {
        var model = new CreateServerPreviewViewModel();

        model.SelectedIntent = CreationIntentCatalog.For(intent);

        Assert.True(model.NextCommand.CanExecute(null));
        model.NextCommand.Execute(null);
        Assert.Equal(CreationWizardStep.Setup, model.CurrentStep);
        Assert.True(model.ShowsBack);
    }

    [Fact]
    public void Back_from_setup_returns_to_the_intent_step_and_keeps_the_choice()
    {
        var model = Choose(CreationIntent.Mods);
        model.NextCommand.Execute(null);

        model.BackCommand.Execute(null);

        Assert.Equal(CreationWizardStep.Intent, model.CurrentStep);
        Assert.Equal(CreationIntent.Mods, model.SelectedIntent?.Intent);
    }

    [Fact]
    public void Back_from_review_preserves_every_valid_selection()
    {
        var model = Ready(CreationIntent.Mods, "Machines and magic", "synthetic-fabric-1214");
        model.NextCommand.Execute(null);
        Assert.Equal(CreationWizardStep.Review, model.CurrentStep);

        model.BackCommand.Execute(null);

        Assert.Equal(CreationWizardStep.Setup, model.CurrentStep);
        Assert.Equal("Machines and magic", model.ServerName);
        Assert.Equal("synthetic-fabric-1214", model.SelectedOption?.Id);
        Assert.True(model.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Changing_intent_clears_the_option_but_keeps_the_server_name()
    {
        var model = Ready(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release");

        model.SelectedIntent = CreationIntentCatalog.For(CreationIntent.Plugins);

        Assert.Equal("Sunday survival", model.ServerName);
        Assert.Null(model.SelectedOption);
        Assert.Null(model.SelectedProject);
        Assert.All(model.Options, option => Assert.Equal(CreationIntent.Plugins, option.Intent));
    }

    [Fact]
    public void Changing_intent_clears_the_advanced_acknowledgement()
    {
        var model = Choose(CreationIntent.Advanced);
        model.ServerName = "Test rig";
        model.AdvancedAcknowledged = true;

        model.SelectedIntent = CreationIntentCatalog.For(CreationIntent.Vanilla);

        Assert.False(model.AdvancedAcknowledged);
    }

    [Fact]
    public void The_domain_record_expresses_the_same_intent_change_rule()
    {
        var selection = new CreationSelection
        {
            Intent = CreationIntent.Mods,
            ServerName = "Kept",
            OptionId = "dropped",
            ProjectId = "dropped",
            AdvancedAcknowledged = true
        };

        var changed = selection.WithIntent(CreationIntent.Modpack);

        Assert.Equal(CreationIntent.Modpack, changed.Intent);
        Assert.Equal("Kept", changed.ServerName);
        Assert.Equal("", changed.OptionId);
        Assert.Equal("", changed.ProjectId);
        Assert.False(changed.AdvancedAcknowledged);
        Assert.Same(selection, selection.WithIntent(CreationIntent.Mods));
    }

    [Fact]
    public void Finishing_the_preview_reaches_the_completion_step_and_offers_only_close()
    {
        var model = Ready(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release");
        model.NextCommand.Execute(null);

        Assert.True(model.FinishPreviewCommand.CanExecute(null));
        model.FinishPreviewCommand.Execute(null);

        Assert.Equal(CreationWizardStep.Completion, model.CurrentStep);
        Assert.True(model.IsCompleted);
        Assert.True(model.ShowsClose);
        Assert.False(model.ShowsBack);
        Assert.False(model.ShowsCancel);
        Assert.False(model.ShowsNext);
        Assert.False(model.ShowsFinish);
    }

    [Fact]
    public void Cancel_asks_the_window_to_close_and_changes_no_state()
    {
        var model = Ready(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release");
        var closed = 0;
        model.CloseRequested += (_, _) => closed++;

        model.CancelCommand.Execute(null);

        Assert.Equal(1, closed);
        Assert.Equal(CreationWizardStep.Setup, model.CurrentStep);
        Assert.False(model.IsCompleted);
    }

    [Fact]
    public void Closing_from_the_completion_step_also_asks_the_window_to_close()
    {
        var model = Ready(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release");
        model.NextCommand.Execute(null);
        model.FinishPreviewCommand.Execute(null);
        var closed = 0;
        model.CloseRequested += (_, _) => closed++;

        model.ClosePreviewCommand.Execute(null);

        Assert.Equal(1, closed);
    }

    [Fact]
    public void Each_step_transition_asks_for_focus_to_move()
    {
        var model = Choose(CreationIntent.Vanilla);
        var requested = new List<CreationWizardStep>();
        model.FocusRequested += (_, step) => requested.Add(step);

        model.NextCommand.Execute(null);
        model.BackCommand.Execute(null);

        Assert.Equal([CreationWizardStep.Setup, CreationWizardStep.Intent], requested);
    }

    [Fact]
    public void The_step_rail_marks_completed_and_current_steps_in_text()
    {
        var model = Ready(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release");

        var intentStep = model.Steps.Single(step => step.Step == CreationWizardStep.Intent);
        var setupStep = model.Steps.Single(step => step.Step == CreationWizardStep.Setup);
        var reviewStep = model.Steps.Single(step => step.Step == CreationWizardStep.Review);

        Assert.True(intentStep.IsComplete);
        Assert.True(setupStep.IsCurrent);
        Assert.False(reviewStep.IsComplete);
        Assert.Contains("completed", intentStep.AutomationName, StringComparison.Ordinal);
        Assert.Contains("current step", setupStep.AutomationName, StringComparison.Ordinal);
        Assert.Contains("not started", reviewStep.AutomationName, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ per-intent setup surface

    [Fact]
    public void The_setup_step_shows_only_the_controls_that_apply_to_the_intent()
    {
        var vanilla = Choose(CreationIntent.Vanilla);
        Assert.True(vanilla.ShowsOptionList);
        Assert.False(vanilla.ShowsProjectBrowser);
        Assert.False(vanilla.ShowsAdvancedSummary);
        Assert.False(vanilla.ShowsCrossplayExplanation);

        var modpack = Choose(CreationIntent.Modpack);
        Assert.False(modpack.ShowsOptionList);
        Assert.True(modpack.ShowsProjectBrowser);
        Assert.False(modpack.ShowsProjectVersions);

        var crossplay = Choose(CreationIntent.Crossplay);
        Assert.True(crossplay.ShowsCrossplayExplanation);
        Assert.False(crossplay.ShowsModExplanation);

        var advanced = Choose(CreationIntent.Advanced);
        Assert.True(advanced.ShowsAdvancedSummary);
        Assert.False(advanced.ShowsOptionList);
        Assert.NotEmpty(advanced.AdvancedCategories);
    }

    [Fact]
    public void Choosing_a_modpack_project_reveals_its_releases_and_clears_any_earlier_release()
    {
        var model = Choose(CreationIntent.Modpack);
        model.SelectedProject = model.Projects.Single(project => project.Id == "synthetic-pack-skyward");
        model.SelectedOption = model.ProjectVersions[0];

        model.SelectedProject = model.Projects.Single(project => project.Id == "synthetic-pack-lantern");

        Assert.True(model.ShowsProjectVersions);
        Assert.Null(model.SelectedOption);
        Assert.All(model.ProjectVersions, option => Assert.Equal("synthetic-pack-lantern", option.ProjectId));
    }

    [Fact]
    public void Searching_filters_the_example_projects_and_reports_an_empty_result_honestly()
    {
        var model = Choose(CreationIntent.Modpack);

        model.ProjectSearch = "Lantern";
        Assert.Single(model.Projects);
        Assert.False(model.HasNoProjectMatches);

        model.ProjectSearch = "a project that does not exist";
        Assert.Empty(model.Projects);
        Assert.True(model.HasNoProjectMatches);

        model.ProjectSearch = "";
        Assert.Equal(SyntheticPreviewCatalog.ModpackProjects.Count, model.Projects.Count);
    }

    [Fact]
    public void The_advanced_step_offers_no_editor_only_a_description_of_what_is_coming()
    {
        var model = Choose(CreationIntent.Advanced);

        Assert.Contains("later update", model.AdvancedNotYetBuilt, StringComparison.OrdinalIgnoreCase);
        Assert.All(model.AdvancedCategories, category =>
        {
            Assert.False(string.IsNullOrWhiteSpace(category.Title));
            Assert.False(string.IsNullOrWhiteSpace(category.Description));
        });
    }

    // ------------------------------------------------------------------ name validation

    [Fact]
    public void A_missing_intent_blocks_the_first_step()
    {
        var validation = CreationPreviewValidator.Validate(new CreationSelection(), null);

        Assert.False(validation.CanContinueFrom(CreationWizardStep.Intent));
        Assert.False(validation.CanFinish);
        Assert.Single(validation.BlockingIssues);
        Assert.Equal(CreationNamePolicy.Fields.Intent, validation.BlockingIssues[0].Field);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \t")]
    public void An_empty_or_whitespace_only_name_blocks(string name)
    {
        var issues = CreationNamePolicy.Validate(name);

        var blocking = issues.Where(issue => issue.Severity == CreationIssueSeverity.Blocking).ToList();
        Assert.Single(blocking);
        Assert.Equal(CreationNamePolicy.Fields.ServerName, blocking[0].Field);
    }

    [Fact]
    public void Surrounding_spaces_are_removed_and_reported_without_blocking()
    {
        var issues = CreationNamePolicy.Validate("  Sunday survival  ");

        Assert.DoesNotContain(issues, issue => issue.Severity == CreationIssueSeverity.Blocking);
        Assert.Contains(issues, issue =>
            issue.Severity == CreationIssueSeverity.Info &&
            issue.Message.Contains("removed", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("my:server")]
    [InlineData("my/server")]
    [InlineData("my\\server")]
    [InlineData("my*server")]
    [InlineData("my?server")]
    [InlineData("my\"server")]
    [InlineData("my<server>")]
    [InlineData("my|server")]
    public void Invalid_windows_filename_characters_block(string name)
    {
        var issues = CreationNamePolicy.Validate(name);

        Assert.Contains(issues, issue =>
            issue.Severity == CreationIssueSeverity.Blocking &&
            issue.Message.Contains("cannot contain", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("aux")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("CON.txt")]
    [InlineData("PRN.world.backup")]
    public void Reserved_windows_device_names_block(string name)
    {
        var issues = CreationNamePolicy.Validate(name);

        Assert.Contains(issues, issue =>
            issue.Severity == CreationIssueSeverity.Blocking &&
            issue.Message.Contains("reserves", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Sunday survival.")]
    [InlineData(".")]
    [InlineData("..")]
    public void A_trailing_full_stop_blocks(string name)
    {
        var issues = CreationNamePolicy.Validate(name);

        Assert.Contains(issues, issue =>
            issue.Severity == CreationIssueSeverity.Blocking &&
            issue.Message.Contains("end with a full stop", StringComparison.Ordinal));
    }

    [Fact]
    public void A_name_longer_than_the_folder_limit_blocks_and_the_limit_itself_is_accepted()
    {
        var atLimit = new string('a', CreationNamePolicy.MaximumLength);
        var overLimit = new string('a', CreationNamePolicy.MaximumLength + 1);

        Assert.Empty(CreationNamePolicy.Validate(atLimit));
        Assert.Contains(CreationNamePolicy.Validate(overLimit), issue =>
            issue.Severity == CreationIssueSeverity.Blocking &&
            issue.Message.Contains(CreationNamePolicy.MaximumLength.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void A_name_that_only_needs_trimming_is_accepted_by_the_wizard()
    {
        var model = Ready(CreationIntent.Vanilla, "  Sunday survival  ", "synthetic-vanilla-release");

        Assert.True(model.NextCommand.CanExecute(null));
        Assert.Single(model.NameMessages);
    }

    // ------------------------------------------------------------------ option validation

    [Fact]
    public void A_missing_option_blocks_the_setup_step()
    {
        var model = Choose(CreationIntent.Vanilla);
        model.ServerName = "Sunday survival";
        model.NextCommand.Execute(null);

        Assert.False(model.NextCommand.CanExecute(null));
        Assert.Contains(model.BlockingMessages, message =>
            message.Contains("Minecraft version", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_modpack_project_blocks_before_a_release_is_even_asked_for()
    {
        var model = Choose(CreationIntent.Modpack);
        model.ServerName = "Weekend pack";
        model.NextCommand.Execute(null);

        Assert.False(model.NextCommand.CanExecute(null));
        Assert.Contains(model.BlockingMessages, message =>
            message.Contains("Choose a modpack", StringComparison.Ordinal));
        Assert.DoesNotContain(model.BlockingMessages, message =>
            message.Contains("Choose a release", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unavailable_option_blocks_with_its_own_reason_rather_than_a_generic_one()
    {
        var model = Ready(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-unavailable");

        Assert.False(model.NextCommand.CanExecute(null));
        Assert.Contains(model.BlockingMessages, message =>
            message.Contains("cannot be chosen", StringComparison.Ordinal));
    }

    [Fact]
    public void A_verified_incompatible_option_blocks()
    {
        var model = Ready(CreationIntent.Mods, "Machines and magic", "synthetic-fabric-1710");

        Assert.False(model.NextCommand.CanExecute(null));
        Assert.False(model.Validation.CanFinish);
        Assert.Contains(model.BlockingMessages, message =>
            message.Contains("Not compatible", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("synthetic-pack-lantern", "synthetic-pack-lantern-20", "No server pack")]
    [InlineData("synthetic-pack-vault", "synthetic-pack-vault-31", "Needs a provider account key")]
    [InlineData("synthetic-pack-expedition", "synthetic-pack-expedition-15", "Needs files you supply")]
    [InlineData("synthetic-pack-relic", "synthetic-pack-relic-42", "Not supported by ChunkPilot")]
    public void Modpack_states_that_cannot_be_installed_block_and_say_why(
        string projectId, string releaseId, string expected)
    {
        var model = ReadyModpack(projectId, releaseId);

        Assert.False(model.NextCommand.CanExecute(null));
        Assert.Contains(model.BlockingMessages, message =>
            message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_compatibility_warns_and_never_appears_verified()
    {
        var model = ReadyModpack("synthetic-pack-driftwood", "synthetic-pack-driftwood-07");

        Assert.True(model.NextCommand.CanExecute(null));
        Assert.True(model.HasWarningMessages);
        Assert.Equal(CompatibilityConclusion.Unknown, model.Context.Compatibility.Conclusion);
        Assert.DoesNotContain(model.WarningMessages, message =>
            message.Contains("Verified compatible", StringComparison.Ordinal));
    }

    [Fact]
    public void Inferred_compatibility_warns_without_blocking()
    {
        var model = Ready(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-snapshot");

        Assert.True(model.NextCommand.CanExecute(null));
        Assert.Equal(CompatibilityConclusion.Inferred, model.Context.Compatibility.Conclusion);
        Assert.Contains(model.WarningMessages, message =>
            message.Contains("Likely compatible", StringComparison.Ordinal));
    }

    [Fact]
    public void A_provider_assertion_is_a_notice_not_a_warning_and_is_never_called_verified()
    {
        var model = Ready(CreationIntent.Plugins, "Village square", "synthetic-paper-1214");

        Assert.True(model.NextCommand.CanExecute(null));
        Assert.Equal(CompatibilityConclusion.ProviderDeclaredCompatible, model.Context.Compatibility.Conclusion);
        Assert.Contains(model.NoticeMessages, message =>
            message.Contains("has not independently confirmed", StringComparison.Ordinal) ||
            message.Contains("has not started it", StringComparison.Ordinal));
    }

    [Fact]
    public void Crossplay_warns_that_public_reachability_is_a_separate_question()
    {
        var model = Ready(CreationIntent.Crossplay, "Family world", "synthetic-crossplay-paper");

        Assert.True(model.NextCommand.CanExecute(null));
        Assert.Contains(model.WarningMessages, message =>
            message.Contains("public reachability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_advanced_path_requires_an_explicit_acknowledgement()
    {
        var model = Choose(CreationIntent.Advanced);
        model.ServerName = "Test rig";
        model.NextCommand.Execute(null);

        Assert.False(model.NextCommand.CanExecute(null));
        Assert.Contains(model.BlockingMessages, message =>
            message.Contains("cannot verify", StringComparison.Ordinal));

        model.AdvancedAcknowledged = true;

        Assert.True(model.NextCommand.CanExecute(null));
        Assert.Contains(model.WarningMessages, message =>
            message.Contains("Custom choices", StringComparison.Ordinal));
    }

    [Fact]
    public void The_final_preview_action_stays_unavailable_while_anything_blocks()
    {
        var model = Ready(CreationIntent.Mods, "Machines and magic", "synthetic-fabric-1214");
        model.NextCommand.Execute(null);
        Assert.True(model.FinishPreviewCommand.CanExecute(null));

        model.ServerName = "bad:name";

        Assert.False(model.FinishPreviewCommand.CanExecute(null));
        Assert.False(model.Validation.CanFinish);
    }

    // ------------------------------------------------------------------ compatibility vocabulary

    [Fact]
    public void Every_compatibility_conclusion_carries_text_at_both_lengths()
    {
        foreach (var conclusion in Enum.GetValues<CompatibilityConclusion>())
        {
            Assert.False(string.IsNullOrWhiteSpace(CompatibilityConclusionPolicy.BadgeLabel(conclusion)));
            Assert.False(string.IsNullOrWhiteSpace(CompatibilityConclusionPolicy.ShortLabel(conclusion)));
            Assert.False(string.IsNullOrWhiteSpace(CompatibilityConclusionPolicy.Explanation(conclusion)));
        }
    }

    [Fact]
    public void The_default_conclusion_is_unknown_so_an_unset_value_never_reads_as_healthy()
    {
        Assert.Equal(CompatibilityConclusion.Unknown, new CompatibilityEvidence().Conclusion);
        Assert.Equal(CompatibilityConclusion.Unknown, (CompatibilityConclusion)0);
        Assert.Equal(CompatibilityConclusion.Unknown, new ResolvedCreationContext().Compatibility.Conclusion);
    }

    [Fact]
    public void Blocking_conclusions_are_exactly_the_states_that_cannot_be_installed()
    {
        var blocking = Enum.GetValues<CompatibilityConclusion>()
            .Where(CompatibilityConclusionPolicy.IsBlocking)
            .ToArray();

        Assert.Equal(
            [
                CompatibilityConclusion.VerifiedIncompatible,
                CompatibilityConclusion.TemporarilyUnavailable,
                CompatibilityConclusion.UnsupportedByChunkPilot,
                CompatibilityConclusion.RequiresAuthentication,
                CompatibilityConclusion.RequiresUserSuppliedArtifact,
                CompatibilityConclusion.NoServerPackAvailable
            ],
            blocking);
    }

    [Fact]
    public void The_synthetic_catalogue_demonstrates_every_compatibility_conclusion()
    {
        var covered = SyntheticPreviewCatalog.AllOptions
            .Select(option => option.Evidence.Conclusion)
            .Distinct()
            .ToHashSet();

        var missing = Enum.GetValues<CompatibilityConclusion>().Except(covered).ToArray();
        Assert.True(missing.Length == 0, "Conclusions with no example: " + string.Join(", ", missing));
    }

    // ------------------------------------------------------------------ review

    [Fact]
    public void The_vanilla_review_omits_loader_and_modpack_lines()
    {
        var review = ReviewFor(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release");

        Assert.Equal("Sunday survival", Row(review, "Server name").Value);
        Assert.Equal("Just Minecraft", Row(review, "What it is for").Value);
        Assert.Equal("1.21.4", Row(review, "Minecraft version").Value);
        Assert.Equal("Vanilla", Row(review, "Server software").Value);
        Assert.Null(FindRow(review, "Mod loader"));
        Assert.Null(FindRow(review, "Modpack"));
    }

    [Fact]
    public void The_plugins_review_names_the_implementation()
    {
        var review = ReviewFor(CreationIntent.Plugins, "Village square", "synthetic-paper-1214");

        Assert.Equal("Paper", Row(review, "Server software").Value);
        Assert.Equal("Provider states compatible", Row(review, "Compatibility").Value);
        Assert.Null(FindRow(review, "Mod loader"));
    }

    [Fact]
    public void The_mods_review_includes_the_loader_and_its_version()
    {
        var review = ReviewFor(CreationIntent.Mods, "Machines and magic", "synthetic-fabric-1214");

        Assert.Equal("Fabric 0.16.9", Row(review, "Mod loader").Value);
        Assert.Equal("Verified compatible", Row(review, "Compatibility").Value);
    }

    [Fact]
    public void The_modpack_review_names_the_project_and_the_release()
    {
        var model = ReadyModpack("synthetic-pack-skyward", "synthetic-pack-skyward-14");
        var review = model.Review;

        Assert.Equal("Sample Pack: Skyward Depths", Row(review, "Modpack").Value);
        Assert.Contains("1.4.0", Row(review, "Release").Value, StringComparison.Ordinal);
        Assert.Null(FindRow(review, "Chosen option"));
    }

    [Fact]
    public void The_crossplay_review_explains_the_two_editions_and_carries_the_networking_warning()
    {
        var model = Ready(CreationIntent.Crossplay, "Family world", "synthetic-crossplay-paper");
        var review = model.Review;

        Assert.Contains("Geyser", Row(review, "Server software").Value, StringComparison.Ordinal);
        var note = review.Sections.SelectMany(section => section.Notes)
            .Single(entry => entry.Label == "Players need");
        Assert.Contains("Bedrock", note.Text, StringComparison.Ordinal);
        Assert.Contains(review.Warnings, warning =>
            warning.Contains("public reachability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_advanced_review_reports_an_unknown_conclusion_rather_than_a_reassuring_one()
    {
        var model = Choose(CreationIntent.Advanced);
        model.ServerName = "Test rig";
        model.AdvancedAcknowledged = true;

        Assert.Equal("Unknown", Row(model.Review, "Compatibility").Value);
        Assert.Contains(model.Review.Warnings, warning =>
            warning.Contains("Custom choices", StringComparison.Ordinal));
    }

    [Fact]
    public void The_review_reflects_the_current_state_after_going_back_and_changing_it()
    {
        var model = Ready(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release");
        model.NextCommand.Execute(null);
        Assert.Equal("Sunday survival", Row(model.Review, "Server name").Value);

        model.BackCommand.Execute(null);
        model.ServerName = "Monday survival";
        model.SelectedOption = model.Options.Single(option => option.Id == "synthetic-vanilla-snapshot");
        model.NextCommand.Execute(null);

        Assert.Equal("Monday survival", Row(model.Review, "Server name").Value);
        Assert.Equal("25w05a", Row(model.Review, "Minecraft version").Value);
        Assert.Equal("Likely compatible", Row(model.Review, "Compatibility").Value);
    }

    [Fact]
    public void The_review_lists_blocking_issues_and_warnings_separately()
    {
        var model = Ready(CreationIntent.Mods, "Machines and magic", "synthetic-fabric-1710");

        Assert.NotEmpty(model.Review.BlockingIssues);
        Assert.DoesNotContain(model.Review.Warnings, warning => model.Review.BlockingIssues.Contains(warning));
    }

    [Fact]
    public void The_review_evidence_admits_that_nothing_was_retrieved_or_hashed()
    {
        var model = Ready(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release");
        var evidence = model.Review.EvidenceRows;

        Assert.True(evidence.Single(row => row.Label == "Provider data retrieved").IsUnknown);
        Assert.True(evidence.Single(row => row.Label == "File hash").IsUnknown);
        Assert.Equal("None supplied", evidence.Single(row => row.Label == "File hash").UnknownText);
        Assert.Contains(model.Review.EvidenceNotes, note =>
            note.Text.Contains("No provider was contacted", StringComparison.Ordinal));
    }

    [Fact]
    public void The_review_never_claims_that_anything_was_done()
    {
        string[] forbidden =
        [
            "installation complete", "installed successfully", "created successfully",
            "download complete", "hash verified", "eula accepted", "server registered",
            "java is installed", "files were created"
        ];

        foreach (var intent in Enum.GetValues<CreationIntent>())
        {
            var model = intent == CreationIntent.Modpack
                ? ReadyModpack("synthetic-pack-skyward", "synthetic-pack-skyward-14")
                : Ready(intent, "Sunday survival", FirstOptionId(intent));
            var text = string.Join(" ", Flatten(model.Review)).ToLowerInvariant();

            foreach (var phrase in forbidden)
                Assert.DoesNotContain(phrase, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_review_states_that_this_is_a_preview_and_lists_what_is_still_unresolved()
    {
        var model = Ready(CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release");

        Assert.Contains("no server is created or registered", model.Review.PreviewNotice, StringComparison.Ordinal);
        Assert.NotEmpty(model.Review.UnresolvedRequirements);
        Assert.Contains(model.Review.UnresolvedRequirements, requirement =>
            requirement.Contains("EULA", StringComparison.Ordinal));
    }

    [Fact]
    public void A_review_with_no_option_chosen_shows_no_compatibility_section_at_all()
    {
        var model = Choose(CreationIntent.Vanilla);
        model.ServerName = "Sunday survival";

        Assert.DoesNotContain(model.Review.Sections, section => section.Title == "Will it work");
        Assert.Empty(model.Review.EvidenceRows);
        Assert.Empty(model.Review.EvidenceNotes);
    }

    // ------------------------------------------------------------------ helpers

    private static CreateServerPreviewViewModel Choose(CreationIntent intent) =>
        new() { SelectedIntent = CreationIntentCatalog.For(intent) };

    private static CreateServerPreviewViewModel Ready(CreationIntent intent, string name, string optionId)
    {
        var model = Choose(intent);
        model.ServerName = name;
        if (!string.IsNullOrEmpty(optionId))
            model.SelectedOption = model.Options.Single(option => option.Id == optionId);
        model.NextCommand.Execute(null);
        return model;
    }

    private static CreateServerPreviewViewModel ReadyModpack(string projectId, string releaseId)
    {
        var model = Choose(CreationIntent.Modpack);
        model.ServerName = "Weekend pack";
        model.NextCommand.Execute(null);
        model.SelectedProject = model.Projects.Single(project => project.Id == projectId);
        model.SelectedOption = model.ProjectVersions.Single(option => option.Id == releaseId);
        return model;
    }

    private static string FirstOptionId(CreationIntent intent)
    {
        var options = SyntheticPreviewCatalog.OptionsFor(intent);
        return options.Count > 0 ? options[0].Id : "";
    }

    private static CreationReviewSummary ReviewFor(CreationIntent intent, string name, string optionId) =>
        Ready(intent, name, optionId).Review;

    private static CreationReviewRow Row(CreationReviewSummary review, string label) =>
        FindRow(review, label) ?? throw new InvalidOperationException($"No review row labelled \"{label}\".");

    private static CreationReviewRow? FindRow(CreationReviewSummary review, string label) =>
        review.Sections.SelectMany(section => section.Rows)
            .FirstOrDefault(row => row.Label == label);

    private static IEnumerable<string> Flatten(CreationReviewSummary review)
    {
        foreach (var section in review.Sections)
        {
            yield return section.Title;
            foreach (var row in section.Rows)
            {
                yield return row.Label;
                yield return row.IsUnknown ? row.UnknownText : row.Value;
            }
            foreach (var note in section.Notes)
            {
                yield return note.Label;
                yield return note.Text;
            }
        }
        foreach (var row in review.EvidenceRows)
            yield return row.IsUnknown ? row.UnknownText : row.Value;
        foreach (var note in review.EvidenceNotes)
            yield return note.Text;
        foreach (var entry in review.Warnings.Concat(review.BlockingIssues).Concat(review.UnresolvedRequirements))
            yield return entry;
        yield return review.PreviewNotice;
    }
}
