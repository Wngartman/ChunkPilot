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
    public void Installer_is_self_contained_per_user_and_exposes_preview_truthfully()
    {
        var source = File.ReadAllText(Path.Combine(Root, "installer", "ChunkPilot.iss"));
        Assert.Contains("PrivilegesRequired=lowest", source, StringComparison.Ordinal);
        Assert.Contains("artifacts\\self-contained-win-x64", source, StringComparison.Ordinal);
        Assert.Contains("ChunkPilot WebUI Preview", source, StringComparison.Ordinal);
        Assert.Contains("--webui-preview", source, StringComparison.Ordinal);
        Assert.Contains("F3017226-FE2A-4295-8BDF-00C3A9A7E4C5", source, StringComparison.Ordinal);
        Assert.Contains("MicrosoftEdgeWebview2Setup.exe", source, StringComparison.Ordinal);
        Assert.Contains("RemoveManagedCheck.Checked := False", source, StringComparison.Ordinal);
        Assert.Contains("RemoveBackupsCheck.Checked := False", source, StringComparison.Ordinal);
        Assert.Contains("procedure InitializeUninstallProgressForm;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function InitializeUninstall(): Boolean;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_publication_is_manual_and_write_permission_is_isolated()
    {
        var release = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));
        var ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
        Assert.Contains("workflow_dispatch:", release, StringComparison.Ordinal);
        Assert.DoesNotContain("push:\n    tags", release.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", release.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("contents: write", release, StringComparison.Ordinal);
        Assert.Contains("needs: build-and-test", release, StringComparison.Ordinal);
        Assert.Contains("Redownload and verify public assets", release, StringComparison.Ordinal);
        Assert.Contains("push:", ci, StringComparison.Ordinal);
        Assert.Contains("pull_request:", ci, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", ci, StringComparison.Ordinal);
        Assert.Contains("distribution:", ci, StringComparison.Ordinal);
        Assert.Contains("test-installer.ps1", ci, StringComparison.Ordinal);
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
        Assert.Contains("WebUiPreviewLaunch", source, StringComparison.Ordinal);
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
        Assert.Contains("Pre-alpha", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsigned", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no account system, ads, or telemetry", readme, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(Root, "LICENSE")));
        Assert.False(File.Exists(Path.Combine(Root, "LICENSE.md")));
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}
