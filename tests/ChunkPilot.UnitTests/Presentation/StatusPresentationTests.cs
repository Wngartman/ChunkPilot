using ChunkPilot.App.Presentation;
using ChunkPilot.Core;
using Xunit;

namespace ChunkPilot.App.Tests.Presentation;

public sealed class StatusPresentationTests
{
    // ── ServerUpdateStatus.ToLabel ──

    [Theory]
    [InlineData(ServerUpdateStatus.SourceNotLinked, "Not linked to an update source")]
    [InlineData(ServerUpdateStatus.UpToDate, "Up to date")]
    [InlineData(ServerUpdateStatus.UpdateAvailable, "Update available")]
    [InlineData(ServerUpdateStatus.Checking, "Checking for updates…")]
    [InlineData(ServerUpdateStatus.Downloading, "Downloading update…")]
    [InlineData(ServerUpdateStatus.ReadyToInstall, "Ready to install")]
    [InlineData(ServerUpdateStatus.Updating, "Updating…")]
    [InlineData(ServerUpdateStatus.PendingValidation, "Pending validation")]
    [InlineData(ServerUpdateStatus.UpdateSuccessful, "Update completed")]
    [InlineData(ServerUpdateStatus.UpdateFailed, "Update failed")]
    [InlineData(ServerUpdateStatus.RollbackAvailable, "Rollback available")]
    [InlineData(ServerUpdateStatus.CheckUnavailable, "Update check unavailable")]
    public void ToLabel_returns_correct_label(ServerUpdateStatus status, string expected)
    {
        var actual = ServerUpdateStatusPresentation.ToLabel(status);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ToLabel_returns_fallback_for_out_of_range_enum()
    {
        var outOfRange = (ServerUpdateStatus)9999;
        var actual = ServerUpdateStatusPresentation.ToLabel(outOfRange);
        Assert.Equal("Update status unavailable", actual);
        Assert.DoesNotContain("9999", actual);
    }

    [Fact]
    public void ToLabel_never_returns_raw_enum_name_as_exact_label()
    {
        foreach (ServerUpdateStatus status in Enum.GetValues<ServerUpdateStatus>())
        {
            var label = ServerUpdateStatusPresentation.ToLabel(status);
            Assert.NotEqual(status.ToString(), label);
        }
    }

    [Fact]
    public void ToLabel_no_false_success_on_failure_states()
    {
        var failed = ServerUpdateStatusPresentation.ToLabel(ServerUpdateStatus.UpdateFailed);
        Assert.DoesNotContain("completed", failed.ToLowerInvariant());
        Assert.DoesNotContain("success", failed.ToLowerInvariant());

        var rollback = ServerUpdateStatusPresentation.ToLabel(ServerUpdateStatus.RollbackAvailable);
        Assert.DoesNotContain("completed", rollback.ToLowerInvariant());
    }

    // ── ServerUpdateStatus.ToDetail ──

    [Theory]
    [InlineData(ServerUpdateStatus.UpToDate, null, "Up to date")]
    [InlineData(ServerUpdateStatus.UpToDate, "", "Up to date")]
    [InlineData(ServerUpdateStatus.UpdateAvailable, "1.21.4", "Update available (1.21.4)")]
    [InlineData(ServerUpdateStatus.UpdateFailed, "timeout", "Update failed (timeout)")]
    public void ToDetail_appends_evidence_when_present(ServerUpdateStatus status, string? evidence, string expected)
    {
        var actual = ServerUpdateStatusPresentation.ToDetail(status, evidence);
        Assert.Equal(expected, actual);
    }

    // ── ServerUpdateStatus.TryMapUnknown ──

    [Theory]
    [InlineData(null, "Update status unavailable")]
    [InlineData("", "Update status unavailable")]
    [InlineData("  ", "Update status unavailable")]
    public void TryMapUnknown_handles_null_and_empty(string? unknown, string expected)
    {
        var actual = ServerUpdateStatusPresentation.TryMapUnknown(unknown!);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryMapUnknown_handles_known_case_variations()
    {
        Assert.Equal("Up to date", ServerUpdateStatusPresentation.TryMapUnknown("uptodate"));
        Assert.Equal("Up to date", ServerUpdateStatusPresentation.TryMapUnknown("UPTODATE"));
        Assert.Equal("Update failed", ServerUpdateStatusPresentation.TryMapUnknown("updatefailed"));
        Assert.Equal("Update failed", ServerUpdateStatusPresentation.TryMapUnknown("UpdateFailed"));
    }

    [Theory]
    [InlineData("future_value_xyz", "Future Value Xyz")]
    [InlineData("snake_case_value", "Snake Case Value")]
    [InlineData("kebab-case-value", "Kebab Case Value")]
    [InlineData("mixed_snake-case_Mix", "Mixed Snake Case Mix")]
    [InlineData("UPPER_CASE_VALUE", "Upper Case Value")]
    public void TryMapUnknown_normalizes_unknown_identifiers(string unknown, string expected)
    {
        var actual = ServerUpdateStatusPresentation.TryMapUnknown(unknown);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("_", actual);
        Assert.DoesNotContain("-", actual);
    }

    // ── UpdateOperationState.ToLabel ──

    [Theory]
    [InlineData(UpdateOperationState.Planned, "Update planned")]
    [InlineData(UpdateOperationState.WarningPlayers, "Notifying players…")]
    [InlineData(UpdateOperationState.Saving, "Saving world data…")]
    [InlineData(UpdateOperationState.Stopping, "Stopping server…")]
    [InlineData(UpdateOperationState.Snapshotting, "Creating rollback snapshot…")]
    [InlineData(UpdateOperationState.Downloading, "Downloading update…")]
    [InlineData(UpdateOperationState.Verifying, "Verifying update package…")]
    [InlineData(UpdateOperationState.ReadyToInstall, "Ready to install")]
    [InlineData(UpdateOperationState.Extracting, "Extracting update package…")]
    [InlineData(UpdateOperationState.PlanningMigration, "Planning configuration migration…")]
    [InlineData(UpdateOperationState.BuildingCandidate, "Preparing candidate server…")]
    [InlineData(UpdateOperationState.Switching, "Switching active instance…")]
    [InlineData(UpdateOperationState.Starting, "Starting server…")]
    [InlineData(UpdateOperationState.Querying, "Validating server startup…")]
    [InlineData(UpdateOperationState.PendingValidation, "Pending validation")]
    [InlineData(UpdateOperationState.RollingBack, "Rolling back to previous version…")]
    [InlineData(UpdateOperationState.Completed, "Update completed")]
    [InlineData(UpdateOperationState.Failed, "Update failed")]
    [InlineData(UpdateOperationState.Cancelled, "Update cancelled")]
    public void UpdateOperationState_ToLabel_returns_correct_label(UpdateOperationState state, string expected)
    {
        var actual = UpdateOperationStatePresentation.ToLabel(state);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UpdateOperationState_ToLabel_returns_fallback_for_out_of_range()
    {
        var outOfRange = (UpdateOperationState)9999;
        var actual = UpdateOperationStatePresentation.ToLabel(outOfRange);
        Assert.Equal("Update operation in progress", actual);
        Assert.DoesNotContain("9999", actual);
    }

    [Fact]
    public void UpdateOperationState_ToLabel_never_returns_raw_enum_name_as_exact_label()
    {
        foreach (UpdateOperationState state in Enum.GetValues<UpdateOperationState>())
        {
            var label = UpdateOperationStatePresentation.ToLabel(state);
            Assert.NotEqual(state.ToString(), label);
        }
    }

    [Fact]
    public void UpdateOperationState_ToLabel_no_false_success_on_failure_states()
    {
        var failed = UpdateOperationStatePresentation.ToLabel(UpdateOperationState.Failed);
        Assert.DoesNotContain("completed", failed.ToLowerInvariant());

        var cancelled = UpdateOperationStatePresentation.ToLabel(UpdateOperationState.Cancelled);
        Assert.DoesNotContain("completed", cancelled.ToLowerInvariant());
        Assert.DoesNotContain("success", cancelled.ToLowerInvariant());
    }

    // ── UpdateOperationState.ToDetail ──

    [Theory]
    [InlineData(UpdateOperationState.Downloading, null, "", "Downloading update…")]
    [InlineData(UpdateOperationState.Downloading, 0.0, "", "Downloading update… · 0%")]
    [InlineData(UpdateOperationState.Downloading, 50.5, "", "Downloading update… · 50%")]
    [InlineData(UpdateOperationState.Downloading, 100.0, "", "Downloading update…")]
    [InlineData(UpdateOperationState.Downloading, -1.0, "", "Downloading update…")]
    [InlineData(UpdateOperationState.Downloading, 75.0, "Verifying hashes", "Downloading update… · Verifying hashes · 75%")]
    [InlineData(UpdateOperationState.Completed, 100.0, "Done", "Update completed · Done")]
    public void UpdateOperationState_ToDetail_formats_step_and_percent(
        UpdateOperationState state, double? percent, string step, string expected)
    {
        var actual = UpdateOperationStatePresentation.ToDetail(state, percent, step);
        Assert.Equal(expected, actual);
    }

    // ── UpdateOperationState.TryMapUnknown ──

    [Theory]
    [InlineData(null, "Update operation in progress")]
    [InlineData("", "Update operation in progress")]
    [InlineData("  ", "Update operation in progress")]
    public void UpdateOperationState_TryMapUnknown_handles_null_and_empty(string? unknown, string expected)
    {
        var actual = UpdateOperationStatePresentation.TryMapUnknown(unknown!);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UpdateOperationState_TryMapUnknown_handles_known_case_variations()
    {
        Assert.Equal("Update completed", UpdateOperationStatePresentation.TryMapUnknown("completed"));
        Assert.Equal("Update completed", UpdateOperationStatePresentation.TryMapUnknown("COMPLETED"));
        Assert.Equal("Updatefailed", UpdateOperationStatePresentation.TryMapUnknown("updatefailed"));
    }

    [Theory]
    [InlineData("future_state_xyz", "Future State Xyz")]
    [InlineData("new_operation_state", "New Operation State")]
    public void UpdateOperationState_TryMapUnknown_normalizes_unknown_identifiers(string unknown, string expected)
    {
        var actual = UpdateOperationStatePresentation.TryMapUnknown(unknown);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("_", actual);
        Assert.DoesNotContain("-", actual);
    }

    // ── ActivityActionPresentation.Format ──

    [Fact]
    public void Format_handles_null_action()
    {
        var actual = ActivityActionPresentation.Format(null!);
        Assert.Equal("Activity recorded", actual);
    }

    [Fact]
    public void Format_handles_empty_action()
    {
        var actual = ActivityActionPresentation.Format("");
        Assert.Equal("Activity recorded", actual);
    }

    [Fact]
    public void Format_handles_whitespace_action()
    {
        var actual = ActivityActionPresentation.Format("   ");
        Assert.Equal("Activity recorded", actual);
    }

    [Fact]
    public void Format_preserves_server_icon_action()
    {
        var actual = ActivityActionPresentation.Format("Server icon updated");
        Assert.Equal("Server icon updated", actual);
    }

    [Theory]
    [InlineData("External program: paper.jar", "External program: paper.jar")]
    [InlineData("External program: Purpur-1.20.4.jar", "External program: Purpur-1.20.4.jar")]
    [InlineData("External program:  custom-server.jar  ", "External program: custom-server.jar")]
    [InlineData("external program: lowercase.jar", "External program: lowercase.jar")]
    public void Format_formats_external_program_action(string input, string expected)
    {
        var actual = ActivityActionPresentation.Format(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Format_formats_external_program_with_empty_filename()
    {
        var actual = ActivityActionPresentation.Format("External program: ");
        Assert.Equal("External program ran", actual);
    }

    [Theory]
    [InlineData("Automation: backup-then-restart", "Automation: backup-then-restart")]
    [InlineData("Automation:  MyRecipe  ", "Automation: MyRecipe")]
    [InlineData("automation: lowercase-recipe", "Automation: lowercase-recipe")]
    public void Format_formats_automation_action(string input, string expected)
    {
        var actual = ActivityActionPresentation.Format(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Format_formats_automation_with_empty_recipe_name()
    {
        var actual = ActivityActionPresentation.Format("Automation: ");
        Assert.Equal("Automation rule ran", actual);
    }

    [Theory]
    [InlineData("Start", "Start")]
    [InlineData("Save", "Save")]
    [InlineData("Safe stop", "Safe stop")]
    [InlineData("Safe restart", "Safe restart")]
    [InlineData("Force terminate", "Force terminate")]
    [InlineData("Console command", "Console command")]
    [InlineData("Server-pack update", "Server pack update")]
    [InlineData("Version rollback", "Version rollback")]
    [InlineData("server_icon_check", "Server icon check")]
    [InlineData("server-icon-check", "Server icon check")]
    [InlineData("UPPERCASE", "Uppercase")]
    [InlineData("a", "A")]
    [InlineData("ab", "Ab")]
    [InlineData("abc", "Abc")]
    [InlineData("abcd", "Abcd")]
    public void Format_formats_readable_action(string input, string expected)
    {
        var actual = ActivityActionPresentation.Format(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Start", "Manual", "Start (via Manual)")]
    [InlineData("Save", "Automation", "Save (via Automation)")]
    [InlineData("Server icon updated", "Manual", "Server icon updated (via Manual)")]
    [InlineData("External program: test.jar", "Automation", "External program: test.jar (via Automation)")]
    [InlineData("Automation: backup", "Manual", "Automation: backup (via Manual)")]
    public void Format_appends_source_when_present(string action, string? source, string expected)
    {
        var actual = ActivityActionPresentation.Format(action, source, null);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Format_with_error_still_returns_activity_recorded()
    {
        var actual = ActivityActionPresentation.Format(null, "Agent", "disk full");
        Assert.Equal("Activity recorded (via Agent)", actual);
    }

    // ── ActivityActionPresentation.FormatForDiagnostics ──

    [Fact]
    public void FormatForDiagnostics_includes_raw_for_unknown_action()
    {
        var actual = ActivityActionPresentation.FormatForDiagnostics("unknown_raw_action");
        Assert.Contains("[raw: unknown_raw_action]", actual);
    }

    [Fact]
    public void FormatForDiagnostics_omits_raw_for_standard_actions()
    {
        var standard = new[]
        {
            "Server icon updated",
            "External program: test.jar",
            "Automation: backup"
        };
        foreach (var action in standard)
        {
            var actual = ActivityActionPresentation.FormatForDiagnostics(action);
            Assert.DoesNotContain("[raw:", actual);
            Assert.Equal(ActivityActionPresentation.Format(action), actual);
        }
    }

    [Fact]
    public void FormatForDiagnostics_handles_null()
    {
        var actual = ActivityActionPresentation.FormatForDiagnostics(null!);
        Assert.Equal("Activity recorded", actual);
    }
}
