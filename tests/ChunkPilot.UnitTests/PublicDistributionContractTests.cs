namespace ChunkPilot.UnitTests;

public sealed class PublicDistributionContractTests
{
    private static readonly string Root = RepositoryRoot();

    [Fact]
    public void Import_dialog_has_no_development_machine_special_case()
    {
        var source = File.ReadAllText(Path.Combine(Root, "src", "ChunkPilot.App", "MainViewModel.cs"));
        Assert.Contains("SelectFolder(\"Select an existing Minecraft server folder\", null)", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_is_self_contained_per_user_and_exposes_only_the_current_interface()
    {
        var source = File.ReadAllText(Path.Combine(Root, "installer", "ChunkPilot.iss"));
        Assert.Contains("PrivilegesRequired=lowest", source, StringComparison.Ordinal);
        Assert.Contains("artifacts\\self-contained-win-x64", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Name: \"{autoprograms}\\ChunkPilot\\ChunkPilot WebUI Preview\"", source,
            StringComparison.Ordinal);
        Assert.Contains("[InstallDelete]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--webui-preview", source, StringComparison.Ordinal);
        Assert.Contains("F3017226-FE2A-4295-8BDF-00C3A9A7E4C5", source, StringComparison.Ordinal);
        Assert.Contains("MicrosoftEdgeWebview2Setup.exe", source, StringComparison.Ordinal);
        Assert.Contains("RemoveManagedCheck.Checked := False", source, StringComparison.Ordinal);
        Assert.Contains("RemoveBackupsCheck.Checked := False", source, StringComparison.Ordinal);
        Assert.Contains("procedure InitializeUninstallProgressForm;", source, StringComparison.Ordinal);
        Assert.Contains("if UninstallSilent then", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function InitializeUninstall(): Boolean;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_publication_builds_once_then_tags_and_publishes_the_exact_payload()
    {
        var release = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));
        var ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
        Assert.Contains("workflow_dispatch:", release, StringComparison.Ordinal);
        Assert.DoesNotContain("push:\n    tags", release.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", release.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("contents: write", release, StringComparison.Ordinal);
        Assert.Contains("needs: build-and-test", release, StringComparison.Ordinal);
        Assert.Contains("release_commit:", release, StringComparison.Ordinal);
        Assert.Contains("supersedes:", release, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ inputs.release_commit }}", release, StringComparison.Ordinal);
        Assert.Contains("Build, test, publish, and compile installer once", release, StringComparison.Ordinal);
        Assert.Contains("Upload the one verified release payload", release, StringComparison.Ordinal);
        Assert.Contains("Create or prove exact annotated tag", release, StringComparison.Ordinal);
        Assert.Contains("git tag -a", release, StringComparison.Ordinal);
        Assert.Contains("release-manifest.json", release, StringComparison.Ordinal);
        Assert.Contains("Redownload and verify public assets", release, StringComparison.Ordinal);
        Assert.Contains("release-${{ inputs.tag }}", release, StringComparison.Ordinal);
        Assert.Contains("ChunkPilot-Release-Metadata-$env:RELEASE_TAG.zip", release, StringComparison.Ordinal);
        Assert.Contains("artifacts/previous-release", release, StringComparison.Ordinal);
        Assert.DoesNotContain("-Path previous-release", release, StringComparison.Ordinal);
        Assert.Equal(1, release.Split("scripts/publish.ps1", StringSplitOptions.None).Length - 1);
        Assert.True(
            release.IndexOf("Build, test, publish, and compile installer once", StringComparison.Ordinal) <
            release.IndexOf("Create or prove exact annotated tag", StringComparison.Ordinal));
        Assert.True(
            release.IndexOf("Create or prove exact annotated tag", StringComparison.Ordinal) <
            release.IndexOf("Publish prerelease", StringComparison.Ordinal));
        Assert.DoesNotContain("push:", ci, StringComparison.Ordinal);
        Assert.Contains("pull_request:", ci, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", ci, StringComparison.Ordinal);
        Assert.Contains("steps.scope.outputs.frontend == 'true' || steps.scope.outputs.dotnet == 'true'", ci,
            StringComparison.Ordinal);
        Assert.Contains("./scripts/build-webui.ps1 -SkipTests", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("test-installer.ps1", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_command_refuses_ambiguous_state_and_watches_one_exact_workflow_run()
    {
        var source = File.ReadAllText(Path.Combine(Root, "scripts", "publish-release.ps1"));
        var docs = File.ReadAllText(Path.Combine(Root, "docs", "release", "RELEASING.md"));
        Assert.Contains("git -C $repoRoot", source, StringComparison.Ordinal);
        Assert.Contains("status --porcelain", source, StringComparison.Ordinal);
        Assert.Contains("branch --show-current", source, StringComparison.Ordinal);
        Assert.Contains("ls-remote origin refs/heads/main", source, StringComparison.Ordinal);
        Assert.Contains("tags are immutable", source, StringComparison.Ordinal);
        Assert.Contains("gh workflow run release.yml", source, StringComparison.Ordinal);
        Assert.Contains("gh run watch $runId", source, StringComparison.Ordinal);
        Assert.Contains("build-manifest.json", source, StringComparison.Ordinal);
        Assert.Contains("-Version 1.3.0-alpha.4", docs, StringComparison.Ordinal);
        Assert.Contains("builds once and tests the exact artifacts", docs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Release_notes_are_version_neutral_and_require_explicit_hotfix_content()
    {
        var template = File.ReadAllText(Path.Combine(Root, "release", "RELEASE_NOTES.template.md"));
        var package = File.ReadAllText(Path.Combine(Root, "scripts", "package-release.ps1"));
        Assert.Contains("{{RELEASE_TAG}}", template, StringComparison.Ordinal);
        Assert.Contains("{{INSTALLER_NAME}}", template, StringComparison.Ordinal);
        Assert.Contains("{{HOTFIX_NOTES}}", template, StringComparison.Ordinal);
        Assert.DoesNotContain("v1.3.0-alpha.3", template, StringComparison.Ordinal);
        Assert.Contains("release\\HOTFIX_NOTES.md", package, StringComparison.Ordinal);
        Assert.Contains("ProductVersion $productVersion is not bound to release commit $commit", package,
            StringComparison.Ordinal);
        Assert.Contains("unresolved placeholder", package, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_lifecycle_test_is_runner_only_and_checks_persistent_data()
    {
        var source = File.ReadAllText(Path.Combine(Root, "scripts", "test-installer.ps1"));
        Assert.Contains("GITHUB_ACTIONS", source, StringComparison.Ordinal);
        Assert.Contains("Installer Fixture Server", source, StringComparison.Ordinal);
        Assert.Contains("chunkpilot.db", source, StringComparison.Ordinal);
        Assert.Contains("{C609C59D-FD5A-4A18-91C8-2D04F7177A69}_is1", source, StringComparison.Ordinal);
        Assert.Contains("DisplayVersion -eq '1.3.0'", source, StringComparison.Ordinal);
        Assert.Contains("TrimEnd(", source, StringComparison.Ordinal);
        Assert.Contains("PersistentDataUnchanged", source, StringComparison.Ordinal);
        Assert.Contains("DefaultLaunch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WebUiPreviewLaunch", source, StringComparison.Ordinal);
        Assert.Contains("PreviousReleaseUpgrade", source, StringComparison.Ordinal);
        Assert.Contains("Get-Content -LiteralPath $uninstallLog -Tail 250", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Sbom_packaging_uses_a_pinned_verified_runtime_and_requires_detected_packages()
    {
        var runtime = File.ReadAllText(Path.Combine(Root, "scripts", "install-sbom-runtime.ps1"));
        var package = File.ReadAllText(Path.Combine(Root, "scripts", "package-release.ps1"));
        Assert.Contains("8.0.30", runtime, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", runtime, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", runtime, StringComparison.Ordinal);
        Assert.Contains("packages).Count -le 1", package, StringComparison.Ordinal);
        Assert.Contains("SPDX:2.2", package, StringComparison.Ordinal);
    }

    [Fact]
    public void Inno_installer_normalizes_the_compilers_nonzero_help_exit_code()
    {
        var source = File.ReadAllText(Path.Combine(Root, "scripts", "install-inno-setup.ps1"));
        Assert.Contains("$global:LASTEXITCODE = 0", source, StringComparison.Ordinal);
        Assert.Contains("$helpExitCode -notin @(0, 1)", source, StringComparison.Ordinal);
        Assert.Contains("IsNullOrWhiteSpace($InstallDirectory)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Evergreen_webview_bootstrapper_uses_identity_verification_instead_of_a_stale_hash()
    {
        var source = File.ReadAllText(Path.Combine(Root, "scripts", "acquire-webview2-bootstrapper.ps1"));
        Assert.Contains("https://go.microsoft.com/fwlink/p/?LinkId=2124703", source, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", source, StringComparison.Ordinal);
        Assert.Contains("O=Microsoft Corporation", source, StringComparison.Ordinal);
        Assert.Contains("MicrosoftEdgeUpdateSetup.exe", source, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$expectedSha256", source, StringComparison.Ordinal);
        Assert.Contains("IsNullOrWhiteSpace($Destination)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaged_ui_smoke_allows_a_bounded_cold_WebView_startup()
    {
        var source = File.ReadAllText(Path.Combine(Root, "scripts", "test-packaged-ui-close.ps1"));
        Assert.Contains("$script:AppStartupTimeoutMilliseconds = 45000", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_snapshot_declares_pre_alpha_unsigned_and_no_source_license()
    {
        var readme = File.ReadAllText(Path.Combine(Root, "README.md"));
        Assert.Contains("alpha prerelease", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsigned", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no required ChunkPilot account, no ads, and no telemetry", readme, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(Root, "LICENSE")));
        Assert.False(File.Exists(Path.Combine(Root, "LICENSE.md")));
    }

    [Fact]
    public void Release_metadata_and_signing_are_explicit_and_secret_free()
    {
        var package = File.ReadAllText(Path.Combine(Root, "scripts", "package-release.ps1"));
        var publish = File.ReadAllText(Path.Combine(Root, "scripts", "publish.ps1"));
        var signing = File.ReadAllText(Path.Combine(Root, "scripts", "sign-release.ps1"));
        Assert.Contains("ChunkPilot-Release-Metadata-$ReleaseTag.zip", package, StringComparison.Ordinal);
        Assert.Contains("build-manifest.json", package, StringComparison.Ordinal);
        Assert.Contains("provenance.json", package, StringComparison.Ordinal);
        Assert.Contains("verify-release-signatures.ps1", publish, StringComparison.Ordinal);
        Assert.Contains("CHUNKPILOT_SIGNING_CERT_THUMBPRINT", signing, StringComparison.Ordinal);
        Assert.Contains("TimeStamperCertificate", signing, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", signing, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_file_app_loads_the_tray_icon_from_an_embedded_resource()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src", "ChunkPilot.App", "ChunkPilot.App.csproj"));
        var app = File.ReadAllText(Path.Combine(Root, "src", "ChunkPilot.App", "App.xaml.cs"));

        Assert.Contains("<Resource Include=\"..\\..\\assets\\ChunkPilot.ico\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<Content Include=\"..\\..\\assets\\ChunkPilot.ico\"", project, StringComparison.Ordinal);
        Assert.Contains("pack://application:,,,/Assets/ChunkPilot.ico", app, StringComparison.Ordinal);
        Assert.DoesNotContain("AppContext.BaseDirectory, \"Assets\", \"ChunkPilot.ico\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Published_and_installed_windows_share_one_explicit_app_identity()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src", "ChunkPilot.App", "ChunkPilot.App.csproj"));
        var window = File.ReadAllText(Path.Combine(Root, "src", "ChunkPilot.App", "WebUi", "WebUiWindow.xaml"));
        var app = File.ReadAllText(Path.Combine(Root, "src", "ChunkPilot.App", "App.xaml.cs"));
        var installer = File.ReadAllText(Path.Combine(Root, "installer", "ChunkPilot.iss"));

        Assert.Contains("CopyToPublishDirectory=\"PreserveNewest\"", project, StringComparison.Ordinal);
        Assert.Contains("Name=\"CopyPackagedApplicationIcon\"", project, StringComparison.Ordinal);
        Assert.Contains("DestinationFiles=\"$(PublishDir)Assets\\ChunkPilot.ico\"", project, StringComparison.Ordinal);
        Assert.Contains("Icon=\"pack://application:,,,/Assets/ChunkPilot.ico\"", window, StringComparison.Ordinal);
        Assert.Contains("SetCurrentProcessExplicitAppUserModelID", app, StringComparison.Ordinal);
        Assert.Equal(2, installer.Split("AppUserModelID: \"ChunkPilot.Desktop\"", StringSplitOptions.None).Length - 1);
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}
