using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChunkPilot.Certification;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeRuntimeCertificationTests
{
    [Fact]
    public void HeadlessAgentLaunchUsesNoWindowAndEnvironmentOnlyCredentialPath()
    {
        var root = NewRoot();
        const string unrelatedSecretName = "CHUNKPILOT_CERTIFICATION_SYNTHETIC_SECRET";
        var previousSecret = Environment.GetEnvironmentVariable(unrelatedSecretName);
        try
        {
            Environment.SetEnvironmentVariable(unrelatedSecretName, "must-not-reach-agent");
            var executable = Path.Combine(root, "ChunkPilot.Agent.exe");
            File.WriteAllBytes(executable, []);
            var keyPath = Path.Combine(root, "approved.key");
            var temporary = Path.Combine(root, "temp");
            Directory.CreateDirectory(temporary);
            var start = HeadlessAgentLaunch.CreateStartInfo(new HeadlessAgentLaunchOptions(
                executable,
                Path.Combine(root, "data"),
                Path.Combine(root, "servers"),
                temporary,
                keyPath,
                "test-instance"));

            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
            Assert.True(start.RedirectStandardOutput);
            Assert.True(start.RedirectStandardError);
            Assert.Empty(start.ArgumentList);
            Assert.Equal(Path.GetFullPath(keyPath),
                start.Environment["CHUNKPILOT_CURSEFORGE_KEY_FILE"]);
            Assert.False(start.Environment.ContainsKey(unrelatedSecretName));
            Assert.False(start.Environment.ContainsKey(
                CertificationUpdateFaultInjector.TokenEnvironmentVariable));
            Assert.False(start.Environment.ContainsKey(
                CertificationUpdateFaultInjector.RuntimeRootEnvironmentVariable));

            var updateToken = new string('a', 64);
            var certified = HeadlessAgentLaunch.CreateStartInfo(new HeadlessAgentLaunchOptions(
                executable,
                Path.Combine(root, "data"),
                Path.Combine(root, "servers"),
                temporary,
                keyPath,
                "certified-instance",
                updateToken,
                root));
            Assert.Equal(updateToken, certified.Environment[
                CertificationUpdateFaultInjector.TokenEnvironmentVariable]);
            Assert.Equal(Path.GetFullPath(root), certified.Environment[
                CertificationUpdateFaultInjector.RuntimeRootEnvironmentVariable]);
            Assert.Equal(Path.GetFullPath(temporary), certified.Environment["TEMP"]);
            Assert.Equal(Path.GetFullPath(temporary), certified.Environment["TMP"]);
            Assert.Equal(Path.GetFullPath(temporary),
                certified.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(unrelatedSecretName, previousSecret);
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task ResponseFramesAreBoundedBeforeJsonParsing()
    {
        await using var valid = new MemoryStream(Encoding.UTF8.GetBytes("{\"ok\":true}\r\nignored"));
        Assert.Equal("{\"ok\":true}",
            await NamedPipeCertificationAgentTransport.ReadBoundedResponseFrameAsync(
                valid, CancellationToken.None));

        var oversized = new byte[NamedPipeCertificationAgentTransport.MaximumResponseFrameBytes + 2];
        Array.Fill(oversized, (byte)'a');
        oversized[^1] = (byte)'\n';
        await using var invalid = new MemoryStream(oversized);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NamedPipeCertificationAgentTransport.ReadBoundedResponseFrameAsync(
                invalid, CancellationToken.None));
    }

    [Fact]
    public async Task PayloadBudgetPersistsOnlyOneWayProviderIdentityAndRefusesLockedLedger()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "evidence", "payload-ledger.json");
            var rawSha1 = new string('a', 40);
            var budget = new CurseForgePayloadBudget(path);
            await budget.ReserveAsync(
                Guid.NewGuid(), "client-preflight", "123", "456", 100, rawSha1,
                CancellationToken.None);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(rawSha1, json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("providerIdentityFingerprint", json, StringComparison.Ordinal);

            using var exactPathLock = new FileStream(
                path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var competing = new CurseForgePayloadBudget(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => competing.ReserveAsync(
                Guid.NewGuid(), "official-server-pack", "123", "789", 100,
                new string('b', 40), CancellationToken.None));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task PayloadBudgetGuardsFullExpectedBytesBeforeNextDownload()
    {
        var root = NewRoot();
        try
        {
            var budget = new CurseForgePayloadBudget(Path.Combine(root, "ledger.json"));
            await budget.ReserveAsync(
                Guid.NewGuid(), "client-preflight", "1", "2",
                CurseForgePayloadBudget.MaximumBytes - 1, new string('a', 40),
                CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => budget.ReserveAsync(
                Guid.NewGuid(), "official-server-pack", "1", "3", 2,
                new string('b', 40), CancellationToken.None));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task PayloadBudgetReservesGeneratedPlanAsOneDurableBatch()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "ledger.json");
            var budget = new CurseForgePayloadBudget(path);
            var reservations = new[]
            {
                new CurseForgePayloadReservationRequest(
                    Guid.NewGuid(), "generated-client", "1", "10", 25, new string('a', 40)),
                new CurseForgePayloadReservationRequest(
                    Guid.NewGuid(), "generated-file", "2", "20", 10, new string('b', 40)),
                new CurseForgePayloadReservationRequest(
                    Guid.NewGuid(), "generated-file", "3", "30", 20, new string('c', 40))
            };

            var snapshot = await budget.ReserveBatchAsync(reservations, CancellationToken.None);

            Assert.Equal(55, snapshot.GuardedCumulativeBytes);
            var ledger = JsonSerializer.Deserialize<CurseForgePayloadLedger>(
                await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(ledger);
            Assert.Equal(3, ledger.Entries.Count);
            Assert.Single(ledger.Entries.Select(entry => entry.ReservedAtUtc).Distinct());
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task PayloadBudgetRejectsWholeBatchBeforeWritingWhenAnyReservationIsInvalid()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "ledger.json");
            var budget = new CurseForgePayloadBudget(path);
            var duplicateId = Guid.NewGuid();
            var reservations = new[]
            {
                new CurseForgePayloadReservationRequest(
                    duplicateId, "generated-file", "1", "10", 25, new string('a', 40)),
                new CurseForgePayloadReservationRequest(
                    duplicateId, "generated-file", "2", "20", 10, new string('b', 40))
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                budget.ReserveBatchAsync(reservations, CancellationToken.None));

            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task PayloadBudgetDoesNotPersistAnyPartOfAnOverBudgetBatch()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "ledger.json");
            var budget = new CurseForgePayloadBudget(path);
            await budget.ReserveAsync(
                Guid.NewGuid(), "existing", "1", "10",
                CurseForgePayloadBudget.MaximumBytes - 30, new string('a', 40),
                CancellationToken.None);
            var reservations = new[]
            {
                new CurseForgePayloadReservationRequest(
                    Guid.NewGuid(), "generated-client", "2", "20", 20, new string('b', 40)),
                new CurseForgePayloadReservationRequest(
                    Guid.NewGuid(), "generated-file", "3", "30", 20, new string('c', 40))
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                budget.ReserveBatchAsync(reservations, CancellationToken.None));

            var ledger = JsonSerializer.Deserialize<CurseForgePayloadLedger>(
                await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(ledger);
            Assert.Single(ledger.Entries);
            Assert.Equal("existing", ledger.Entries[0].Kind);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task PayloadBudgetRefusesSerialReplayOfExactTransferReservation()
    {
        var root = NewRoot();
        try
        {
            var budget = new CurseForgePayloadBudget(Path.Combine(root, "ledger.json"));
            var operationId = Guid.NewGuid();
            var providerSha1 = new string('a', 40);
            await budget.ReserveAsync(
                operationId, "client-preflight", "1", "2", 100, providerSha1,
                CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => budget.ReserveAsync(
                operationId, "client-preflight", "1", "2", 100, providerSha1,
                CancellationToken.None));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task PayloadBudgetSeparatesCompletedBytesFromUnknownOrInterruptedReservations()
    {
        var root = NewRoot();
        try
        {
            var budget = new CurseForgePayloadBudget(Path.Combine(root, "ledger.json"));
            var completedId = Guid.NewGuid();
            var interruptedId = Guid.NewGuid();
            await budget.ReserveAsync(
                completedId, "client-preflight", "1", "2", 100,
                new string('a', 40), CancellationToken.None);
            var completed = await budget.CompleteAsync(
                completedId, "client-preflight", 100, new string('c', 64),
                "Ready", transferCompleted: true, CancellationToken.None);
            Assert.Equal(100, completed.CompletedCumulativeBytes);
            Assert.Equal(0, completed.UnknownOrInterruptedReservationCount);

            await budget.ReserveAsync(
                interruptedId, "official-server-pack", "1", "3", 200,
                new string('b', 40), CancellationToken.None);
            var interrupted = await budget.CompleteAsync(
                interruptedId, "official-server-pack", 25, "", "Failed",
                transferCompleted: false, CancellationToken.None);

            Assert.Equal(300, interrupted.GuardedCumulativeBytes);
            Assert.Equal(100, interrupted.CompletedCumulativeBytes);
            Assert.Equal(1, interrupted.UnknownOrInterruptedReservationCount);
            Assert.Equal(200, interrupted.UnknownOrInterruptedReservedBytes);
            Assert.Equal(25, interrupted.ObservedIncompleteBytes);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task PayloadLedgerRejectsOversizedFileAndExcessiveEntryCount()
    {
        var root = NewRoot();
        try
        {
            var oversizedPath = Path.Combine(root, "oversized-ledger.json");
            await File.WriteAllBytesAsync(
                oversizedPath, new byte[CurseForgePayloadBudget.MaximumLedgerBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgePayloadBudget(oversizedPath)
                    .GetSnapshotAsync(CancellationToken.None));

            var excessivePath = Path.Combine(root, "excessive-ledger.json");
            var emptyEntries = string.Join(",",
                Enumerable.Repeat("{}", CurseForgePayloadBudget.MaximumLedgerEntries + 1));
            var excessiveJson =
                $"{{\"documentType\":\"{CurseForgePayloadLedger.ExpectedDocumentType}\"," +
                $"\"schemaVersion\":{CurseForgePayloadLedger.CurrentSchemaVersion}," +
                $"\"budgetBytes\":{CurseForgePayloadBudget.MaximumBytes}," +
                $"\"entries\":[{emptyEntries}]}}";
            Assert.Throws<InvalidDataException>(() =>
                CurseForgePayloadBudget.ValidateSerializedEntryCount(
                    Encoding.UTF8.GetBytes(excessiveJson)));
            await File.WriteAllTextAsync(excessivePath, excessiveJson);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgePayloadBudget(excessivePath)
                    .GetSnapshotAsync(CancellationToken.None));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void OwnedRuntimeRejectsNonemptyUnmarkedAndConcurrentOwnership()
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            var artifacts = Path.Combine(repository, "artifacts");
            var foreign = Path.Combine(artifacts, "foreign");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "unowned.txt"), "foreign");
            Assert.Throws<InvalidOperationException>(() =>
                OwnedCertificationRuntime.Prepare(repository, foreign));

            var owned = Path.Combine(artifacts, "owned");
            var prepared = OwnedCertificationRuntime.Prepare(repository, owned);
            var marker = File.ReadAllText(Path.Combine(
                prepared, OwnedCertificationRuntime.MarkerFileName));
            Assert.DoesNotContain(repository, marker, StringComparison.OrdinalIgnoreCase);
            using var lease = OwnedCertificationRuntime.AcquireExclusiveLease(repository, owned);
            Assert.Throws<InvalidOperationException>(() =>
                OwnedCertificationRuntime.AcquireExclusiveLease(repository, owned));
            Assert.Throws<InvalidOperationException>(() =>
                OwnedCertificationRuntime.Prepare(repository, Path.Combine(root, "outside")));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task MetadataSessionUsesExactAuthorityAndNeverRequestsPayload()
    {
        var root = NewRoot();
        try
        {
            var transport = new MetadataTransport();
            var report = new CurseForgeRuntimeCertificationReport { Phase = "Metadata" };
            var options = Options(root, CurseForgeRuntimeCertificationPhase.Metadata);
            var session = new CurseForgeRuntimeCertificationSession(
                transport,
                new CurseForgePayloadBudget(Path.Combine(root, "payload-ledger.json")),
                static (_, _) => Task.CompletedTask);

            await session.RegisterAndAuthenticateAsync(
                options, report, 1234, 5678, bootstrap: false, CancellationToken.None);
            await session.ExecuteCertifiedWorkAsync(options, report, CancellationToken.None);
            await session.SafeExitAsync(options, report, bootstrap: false);

            Assert.True(report.DpapiRelaunchAuthenticatedCatalogResolve);
            Assert.True(report.Success);
            Assert.True(report.SafeApplicationExitAccepted);
            Assert.Equal("123", report.ProjectId);
            Assert.Equal("456", report.ClientFileId);
            Assert.Contains("ResolveCatalogProject", transport.Operations);
            Assert.DoesNotContain("PreflightCurseForgeModpack", transport.Operations);
            Assert.DoesNotContain("BeginModpackCreation", transport.Operations);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task ConfiguredCredentialDoesNotCountAsDpapiProofWhenAuthenticatedResolveFails()
    {
        var root = NewRoot();
        try
        {
            var transport = new MetadataTransport(failCatalogResolve: true);
            var report = new CurseForgeRuntimeCertificationReport { Phase = "Metadata" };
            var options = Options(root, CurseForgeRuntimeCertificationPhase.Metadata);
            var session = new CurseForgeRuntimeCertificationSession(
                transport,
                new CurseForgePayloadBudget(Path.Combine(root, "payload-ledger.json")),
                static (_, _) => Task.CompletedTask);

            await session.RegisterAndAuthenticateAsync(
                options, report, 1234, 5678, bootstrap: false, CancellationToken.None);
            Assert.True(report.DpapiRelaunchProtectedCredentialConfigured);
            Assert.True(report.DpapiRelaunchProviderConfigured);
            Assert.False(report.DpapiRelaunchAuthenticatedCatalogResolve);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.ExecuteCertifiedWorkAsync(options, report, CancellationToken.None));
            Assert.False(report.DpapiRelaunchAuthenticatedCatalogResolve);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task UpdateFailureArmUsesExactSessionAndMemoryOnlyCapability()
    {
        var root = NewRoot();
        try
        {
            var token = new string('d', 64);
            var transport = new MetadataTransport();
            var report = new CurseForgeRuntimeCertificationReport { Phase = "Full" };
            var options = Options(root, CurseForgeRuntimeCertificationPhase.Metadata);
            var session = new CurseForgeRuntimeCertificationSession(
                transport,
                new CurseForgePayloadBudget(Path.Combine(root, "payload-ledger.json")),
                static (_, _) => Task.CompletedTask,
                certificationUpdateFaultToken: token);
            await session.RegisterAndAuthenticateAsync(
                options, report, 1234, 5678, bootstrap: false, CancellationToken.None);
            var serverId = Guid.NewGuid();
            var operationId = Guid.NewGuid();

            await session.ArmCertificationUpdateFailureAsync(
                options, report, serverId, operationId, CancellationToken.None);

            var request = Assert.IsType<ArmCertificationUpdateFailureRequest>(transport.ArmRequest);
            Assert.Equal(serverId, request.ServerId);
            Assert.Equal(operationId, request.OperationId);
            Assert.Equal(token, request.Token);
            Assert.NotEqual(Guid.Empty, request.SessionId);
            Assert.False(string.IsNullOrWhiteSpace(request.SessionCapability));
            Assert.DoesNotContain(token, JsonSerializer.Serialize(report), StringComparison.Ordinal);

            var failingTransport = new MetadataTransport(armFailure: "echoed " + token);
            var failingReport = new CurseForgeRuntimeCertificationReport { Phase = "Full" };
            var failingSession = new CurseForgeRuntimeCertificationSession(
                failingTransport,
                new CurseForgePayloadBudget(Path.Combine(root, "failed-payload-ledger.json")),
                static (_, _) => Task.CompletedTask,
                certificationUpdateFaultToken: token);
            await failingSession.RegisterAndAuthenticateAsync(
                options, failingReport, 1234, 5678, bootstrap: false, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failingSession.ArmCertificationUpdateFailureAsync(
                    options, failingReport, serverId, Guid.NewGuid(), CancellationToken.None));
            Assert.DoesNotContain(
                token, JsonSerializer.Serialize(failingReport), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void PreflightMustMatchExactOperationAndBuildPlanCarriesSeparateAuthorization()
    {
        var root = NewRoot();
        try
        {
            var project = Project();
            var release = Assert.Single(project.Versions);
            var expectedPreflight = Guid.NewGuid();
            var result = ReadyPreflight(expectedPreflight);
            Assert.Throws<InvalidDataException>(() =>
                CurseForgeRuntimeCertificationSession.ValidatePreflight(
                    Guid.NewGuid(), project, release, result));
            CurseForgeRuntimeCertificationSession.ValidatePreflight(
                expectedPreflight, project, release, result);

            var creation = Guid.NewGuid();
            var plan = CurseForgeRuntimeCertificationSession.BuildPlan(
                Options(root, CurseForgeRuntimeCertificationPhase.Official),
                creation, project, release, result);
            Assert.Equal(creation, plan.OperationId);
            Assert.Equal(expectedPreflight, plan.PreflightOperationId);
            Assert.NotEqual(plan.OperationId, plan.PreflightOperationId);
            Assert.Equal(result.ServerPackDownloadUrl, plan.Source);
            Assert.Equal(result.ServerPackSha1, plan.ExpectedSha1);
            Assert.Empty(plan.Problems());
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void FullCampaignBindsGeneratedAndOfficialUpdateIdentitiesWithoutCallerTrustedPlan()
    {
        var root = NewRoot();
        try
        {
            var generatedRelease = new CatalogVersion
            {
                VersionId = "654",
                ClientFileId = "654",
                VersionName = "Generated fixture",
                ClientDownloadUrl = "https://mediafilez.forgecdn.net/files/654/client.zip",
                ClientSha1 = new string('a', 40),
                ClientSizeBytes = 100,
                CanGenerateServerCandidate = true
            };
            var generatedProject = Project() with
            {
                ProjectId = "321",
                Versions = [generatedRelease]
            };
            var preflightId = Guid.NewGuid();
            var generatedPlan = CurseForgeGeneratedPackPlanService.Seal(
                new CurseForgeGeneratedPackPlan
                {
                    MinecraftVersion = "1.20.1",
                    Loader = "Forge",
                    LoaderVersion = "47.2.0",
                    RequiredFiles =
                    [
                        new CurseForgeGeneratedFilePlan
                        {
                            ProjectId = "12",
                            FileId = "34",
                            FileName = "required.jar",
                            DownloadUrl = "https://mediafilez.forgecdn.net/files/34/required.jar",
                            SizeBytes = 200,
                            ProviderSha1 = new string('b', 40),
                            RequiredBy =
                            [
                                new CurseForgeGeneratedFileEvidence
                                {
                                    Relation = CurseForgeGeneratedFileRelation.ManifestRequired,
                                    RequestedFileId = "34"
                                }
                            ]
                        }
                    ],
                    TotalResolvedBytes = 200
                });
            var preflight = new CurseForgeModpackPreflightResult
            {
                OperationId = preflightId,
                ProjectId = "321",
                ClientFileId = "654",
                State = CatalogReleasePreflightState.Ready,
                MinecraftVersion = "1.20.1",
                Loader = "Forge",
                LoaderVersion = "47.2.0",
                RequiredJavaMajor = 17,
                ClientDownloadUrl = generatedRelease.ClientDownloadUrl,
                ClientSha1 = generatedRelease.ClientSha1,
                ClientSha256 = new string('c', 64),
                ClientSizeBytes = 100,
                GeneratedPackPlan = generatedPlan
            };

            var validated = CurseForgeRuntimeCertificationSession.ValidateGeneratedPreflight(
                preflightId, generatedProject, generatedRelease, preflight);
            var creationId = Guid.NewGuid();
            var creation = CurseForgeRuntimeCertificationSession.BuildGeneratedPlan(
                Options(root, CurseForgeRuntimeCertificationPhase.Official),
                creationId, generatedProject, generatedRelease, preflight);

            Assert.Equal(generatedPlan.Digest, validated.Digest);
            Assert.Equal(creationId, creation.OperationId);
            Assert.Equal(preflightId, creation.PreflightOperationId);
            Assert.Equal(ModpackCreationSource.CurseForgeGeneratedCandidate, creation.SourceKind);
            Assert.Null(creation.CurseForgeGeneratedPlan);
            Assert.Empty(creation.Problems());

            var official = Project();
            var update = CurseForgeRuntimeCertificationSession.BuildUpdateTarget(
                official, Assert.Single(official.Versions));
            Assert.Equal("123", update.PackId);
            Assert.Equal("456", update.VersionId);
            Assert.Equal("789", update.ProviderFileId);
            Assert.Equal(200, update.FileSize);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void FullUpdateRefreshAndMigrationConfirmationStayExactAndOneShot()
    {
        var project = Project();
        var release = Assert.Single(project.Versions);
        var serverId = Guid.NewGuid();
        var target = CurseForgeRuntimeCertificationSession.BuildUpdateTarget(project, release);
        var source = new UpdateSource
        {
            ServerId = serverId,
            Provider = UpdateProvider.CurseForge,
            ProjectId = project.ProjectId,
            InstalledVersionId = "111",
            InstalledVersionName = "Old"
        };
        var check = new UpdateCheckResult
        {
            ServerId = serverId,
            Source = source,
            LatestVersion = target
        };
        CurseForgeRuntimeCertificationSession.ValidateUpdateRefreshIdentity(
            check, serverId, project, release);
        Assert.Throws<InvalidDataException>(() =>
            CurseForgeRuntimeCertificationSession.ValidateUpdateRefreshIdentity(
                check with { LatestVersion = target with { ProviderFileId = "999" } },
                serverId, project, release));

        var reviewId = Guid.NewGuid();
        var review = new UpdateOperationSnapshot
        {
            OperationId = reviewId,
            IsTerminal = true,
            Success = false,
            Progress = new UpdateProgress
            {
                OperationId = reviewId,
                State = UpdateOperationState.PlanningMigration
            },
            Result = new UpdateExecutionResult
            {
                OperationId = reviewId,
                ServerId = serverId,
                TargetVersionId = release.ClientFileId,
                MigrationPlan = new MigrationPlan { Conflicts = ["config/example.toml"] }
            }
        };
        Assert.True(CurseForgeRuntimeCertificationSession.IsMigrationReview(review));
        var confirmedId = Guid.NewGuid();
        var confirmed = CurseForgeRuntimeCertificationSession.BuildConfirmedUpdateRequest(
            confirmedId, serverId, target, reviewId, review.Result.MigrationPlan);
        Assert.Equal(confirmedId, confirmed.OperationId);
        Assert.Equal(reviewId, confirmed.ReviewedOperationId);
        Assert.True(confirmed.ConfirmedMigrationWarnings);
        Assert.False(confirmed.DownloadOnly);
        Assert.Equal(MigrationResolutionKind.NewBaseline,
            confirmed.MigrationResolutions["config/example.toml"].Kind);
    }

    [Fact]
    public void FullManagedContentPlanRequiresDistinctExactCurseForgeReleases()
    {
        var main = new PluginRelease
        {
            Provider = PluginProviderKind.CurseForge,
            ProjectId = "12",
            VersionId = "34",
            DownloadUrl = "https://mediafilez.forgecdn.net/files/34/main.jar",
            FileName = "main.jar",
            SizeBytes = 100,
            Sha1 = new string('a', 40)
        };
        var dependency = main with
        {
            ProjectId = "56",
            VersionId = "78",
            DownloadUrl = "https://mediafilez.forgecdn.net/files/78/dependency.jar",
            FileName = "dependency.jar",
            Sha1 = new string('b', 40)
        };

        Assert.Equal(2,
            CurseForgeRuntimeCertificationSession.ValidateManagedContentPlan(
                [main, dependency]).Count);
        Assert.Throws<InvalidDataException>(() =>
            CurseForgeRuntimeCertificationSession.ValidateManagedContentPlan([main, main]));
        Assert.Throws<InvalidDataException>(() =>
            CurseForgeRuntimeCertificationSession.ValidateManagedContentPlan(
                [main with { DownloadUrl = "https://example.invalid/main.jar" }]));
    }

    [Fact]
    public void FullCampaignKnownPayloadProjectionCountsOnlyRealTransferPathsAndFitsAtomically()
    {
        var projection = CurseForgeRuntimeCertificationSession.CalculateKnownPayloadProjection(
            olderClientBytes: 10,
            olderServerBytes: 20,
            newerClientBytes: 30,
            newerServerBytes: 40,
            generatedClientBytes: 50,
            generatedFallbackClientBytes: 60);

        Assert.Equal(30, projection.OfficialOlderBytes);
        Assert.Equal(170, projection.GeneratedWorstCaseBytes);
        Assert.Equal(200, projection.UpdateWorstCaseBytes);
        Assert.Equal(400, projection.TotalBytes);
        CurseForgeRuntimeCertificationSession.RequireKnownPayloadProjectionFits(
            limitBytes: 500, guardedBytes: 100, projectedBytes: projection.TotalBytes);
        Assert.Throws<InvalidOperationException>(() =>
            CurseForgeRuntimeCertificationSession.RequireKnownPayloadProjectionFits(
                limitBytes: 500, guardedBytes: 101, projectedBytes: projection.TotalBytes));
    }

    [Fact]
    public void GeneratedCreationPlanSizeIncludesArchiveAndEveryRequiredFileBeforeReservation()
    {
        var additional = CurseForgeRuntimeCertificationSession
            .CalculateGeneratedCreationAdditionalBytes(
                clientArchiveBytes: 25,
                requiredFileBytes: [10, 20, 30]);

        Assert.Equal(85, additional);
        CurseForgeRuntimeCertificationSession.RequireKnownPayloadProjectionFits(
            limitBytes: 200, guardedBytes: 115, projectedBytes: additional);
        Assert.Throws<InvalidOperationException>(() =>
            CurseForgeRuntimeCertificationSession.RequireKnownPayloadProjectionFits(
                limitBytes: 200, guardedBytes: 116, projectedBytes: additional));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CurseForgeRuntimeCertificationSession.CalculateGeneratedCreationAdditionalBytes(
                clientArchiveBytes: 25,
                requiredFileBytes: [10, 0]));
    }

    [Fact]
    public void MetadataIdentityCanTruthfullyDescribeAnExactGeneratedCandidateWithoutAServerFile()
    {
        var report = new CurseForgeRuntimeCertificationReport();
        var project = new CatalogItem
        {
            Provider = CatalogProvider.CurseForge,
            ProjectId = "123"
        };
        var release = new CatalogVersion
        {
            VersionId = "456",
            ClientFileId = "456",
            MinecraftVersion = "1.20.1",
            Loader = "Forge",
            LoaderVersion = "47.3.0",
            ClientSizeBytes = 1024,
            CanGenerateServerCandidate = true,
            DistributionAllowed = true,
            RequiredJavaMajor = 17
        };

        CurseForgeRuntimeCertificationSession.BindResolvedIdentity(report, project, release);

        Assert.Equal("123", report.ProjectId);
        Assert.Equal("456", report.ClientFileId);
        Assert.Equal("", report.ServerPackFileId);
        Assert.False(report.ResolvedHasServerPackage);
        Assert.True(report.ResolvedCanGenerateServerCandidate);
        Assert.Equal("1.20.1", report.ResolvedMinecraftVersion);
        Assert.Equal("Forge", report.ResolvedLoader);
        Assert.Equal("47.3.0", report.ResolvedLoaderVersion);
        Assert.Equal(1024, report.ResolvedClientSizeBytes);
        Assert.Equal(17, report.ResolvedRequiredJavaMajor);

        Assert.Throws<InvalidDataException>(() =>
            CurseForgeRuntimeCertificationSession.BindResolvedIdentity(
                new CurseForgeRuntimeCertificationReport(),
                project,
                release with { HasServerPackage = true }));
    }

    [Fact]
    public void FullGeneratedFallbackAndWrongServerInventoryProofAreFailClosed()
    {
        var primary = new CurseForgeExactReleaseSelection
        {
            ProjectReference = "123",
            ClientFileId = "456"
        };
        var options = new CurseForgeFullCampaignOptions
        {
            OfficialNewer = new CurseForgeExactReleaseSelection
            {
                ProjectReference = "123",
                ClientFileId = "789"
            },
            Generated = primary,
            GeneratedFallback = primary,
            ForgeMinecraftVersion = "1.20.1",
            ForgeLoaderVersion = "47.2.0",
            ModProjectId = "12",
            ModFileId = "34"
        };
        Assert.Throws<ArgumentException>(options.Validate);

        var first = new[]
        {
            new ModPluginEntry
            {
                RelativePath = "mods/example.jar",
                FileName = "example.jar",
                SizeBytes = 10,
                Sha256 = new string('a', 64),
                Provider = PluginProviderKind.CurseForge,
                ProviderProjectId = "12",
                ProviderVersionId = "34"
            }
        };
        Assert.True(CurseForgeRuntimeCertificationSession.SameInventory(first, first.Reverse().ToArray()));
        Assert.False(CurseForgeRuntimeCertificationSession.SameInventory(
            first, [first[0] with { Sha256 = new string('b', 64) }]));
    }

    [Fact]
    public void UnsupportedGeneratedPreflightStillRequiresExactDownloadedClientIdentity()
    {
        var release = new CatalogVersion
        {
            ClientFileId = "654",
            ClientDownloadUrl = "https://mediafilez.forgecdn.net/files/654/client.zip",
            ClientSha1 = new string('a', 40),
            ClientSizeBytes = 100,
            CanGenerateServerCandidate = true
        };
        var project = Project() with { ProjectId = "321", Versions = [release] };
        var operationId = Guid.NewGuid();
        var unsupported = new CurseForgeModpackPreflightResult
        {
            OperationId = operationId,
            ProjectId = project.ProjectId,
            ClientFileId = release.ClientFileId,
            State = CatalogReleasePreflightState.Unsupported,
            ClientDownloadUrl = release.ClientDownloadUrl,
            ClientSha1 = release.ClientSha1,
            ClientSha256 = new string('c', 64),
            ClientSizeBytes = release.ClientSizeBytes!.Value
        };

        CurseForgeRuntimeCertificationSession.ValidateGeneratedClientIdentity(
            operationId, project, release, unsupported);
        Assert.Throws<InvalidDataException>(() =>
            CurseForgeRuntimeCertificationSession.ValidateGeneratedClientIdentity(
                Guid.NewGuid(), project, release, unsupported));
    }

    [Fact]
    public void PackageFreshnessRejectsTrackedAndUntrackedSourceChanges()
    {
        var root = NewRoot();
        try
        {
            RunGit(root, "init", "--quiet");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            var tracked = Path.Combine(root, "src", "Tracked.cs");
            File.WriteAllText(tracked, "internal sealed class Tracked {}\n");
            RunGit(root, "add", "src/Tracked.cs");
            RunGit(root, "-c", "user.name=ChunkPilot Tests",
                "-c", "user.email=chunkpilot-tests@example.invalid",
                "commit", "--quiet", "-m", "fixture");
            CertificationPackageFreshness.EnsurePackageSourcesClean(root);

            File.AppendAllText(tracked, "// dirty\n");
            Assert.Throws<InvalidOperationException>(() =>
                CertificationPackageFreshness.EnsurePackageSourcesClean(root));
            File.WriteAllText(tracked, "internal sealed class Tracked {}\n");
            CertificationPackageFreshness.EnsurePackageSourcesClean(root);

            File.WriteAllText(Path.Combine(root, "src", "Untracked.cs"), "// untracked\n");
            Assert.Throws<InvalidOperationException>(() =>
                CertificationPackageFreshness.EnsurePackageSourcesClean(root));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void PackageInputProofRejectsStagedAndSkipWorktreeHiddenDifferences()
    {
        var root = NewRoot();
        try
        {
            RunGit(root, "init", "--quiet");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            const string original = "internal sealed class Tracked {}\n";
            var tracked = Path.Combine(root, "src", "Tracked.cs");
            File.WriteAllText(tracked, original);
            RunGit(root, "add", "src/Tracked.cs");
            RunGit(root, "-c", "user.name=ChunkPilot Tests",
                "-c", "user.email=chunkpilot-tests@example.invalid",
                "commit", "--quiet", "-m", "fixture");

            File.WriteAllText(tracked, original + "// staged\n");
            RunGit(root, "add", "src/Tracked.cs");
            File.WriteAllText(tracked, original);
            Assert.False(CertificationPackageFreshness.ComputePackageInputProof(root).MatchesHead);
            RunGit(root, "add", "src/Tracked.cs");
            Assert.True(CertificationPackageFreshness.ComputePackageInputProof(root).MatchesHead);

            RunGit(root, "update-index", "--skip-worktree", "src/Tracked.cs");
            File.WriteAllText(tracked, original + "// hidden\n");
            Assert.False(CertificationPackageFreshness.ComputePackageInputProof(root).MatchesHead);
            File.WriteAllText(tracked, original);
            Assert.True(CertificationPackageFreshness.ComputePackageInputProof(root).MatchesHead);
            RunGit(root, "update-index", "--no-skip-worktree", "src/Tracked.cs");
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void PackageInputProofRejectsIgnoredSourceButAllowsKnownGeneratedOutputs()
    {
        var root = NewRoot();
        try
        {
            RunGit(root, "init", "--quiet");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(
                Path.Combine(root, "src", "Tracked.cs"),
                "internal sealed class Tracked {}\n");
            RunGit(root, "add", "src/Tracked.cs");
            RunGit(root, "-c", "user.name=ChunkPilot Tests",
                "-c", "user.email=chunkpilot-tests@example.invalid",
                "commit", "--quiet", "-m", "fixture");

            var exclude = Path.Combine(root, ".git", "info", "exclude");
            File.AppendAllText(exclude, "\nsrc/ChunkPilot.App/temp/Injected.cs\n");
            var ignoredSource = Path.Combine(root, "src", "ChunkPilot.App", "temp", "Injected.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(ignoredSource)!);
            File.WriteAllText(ignoredSource, "internal sealed class Injected {}\n");
            Assert.False(CertificationPackageFreshness.ComputePackageInputProof(root).MatchesHead);

            File.Delete(ignoredSource);
            File.AppendAllText(exclude, "src/ChunkPilot.App/obj/Generated.cs\n");
            var generated = Path.Combine(root, "src", "ChunkPilot.App", "obj", "Generated.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
            File.WriteAllText(generated, "// generated intermediate\n");
            Assert.True(CertificationPackageFreshness.ComputePackageInputProof(root).MatchesHead);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void PackageFreshnessRejectsCredentialWrapperThatDiffHidingCannotMask()
    {
        var root = NewRoot();
        try
        {
            RunGit(root, "init", "--quiet");
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.WriteAllText(Path.Combine(root, "scripts", "dev-build.ps1"), "# build\n");
            var wrapper = Path.Combine(root, "scripts", "certify-curseforge-runtime.ps1");
            File.WriteAllText(wrapper, "# credential wrapper\n");
            RunGit(root, "add", "scripts/dev-build.ps1", "scripts/certify-curseforge-runtime.ps1");
            RunGit(root, "-c", "user.name=ChunkPilot Tests",
                "-c", "user.email=chunkpilot-tests@example.invalid",
                "commit", "--quiet", "-m", "fixture");
            var head = RunGitOutput(root, "rev-parse", "HEAD");
            CertificationPackageFreshness.ValidateRepositoryIdentity(root, head);

            RunGit(root, "update-index", "--assume-unchanged",
                "scripts/certify-curseforge-runtime.ps1");
            File.AppendAllText(wrapper, "# hidden dirty change\n");
            Assert.Throws<InvalidOperationException>(() =>
                CertificationPackageFreshness.ValidateRepositoryIdentity(root, head));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void ReportTextSanitizerRemovesCredentialPathProviderUrlAndRawSha1()
    {
        var root = NewRoot();
        try
        {
            var options = Options(root, CurseForgeRuntimeCertificationPhase.Metadata);
            var rawSha1 = new string('d', 40);
            var detail = $"{options.ApprovedKeyFilePath} https://mediafilez.forgecdn.net/files/1/a.zip {rawSha1}";
            var sanitized = options.Sanitize(detail);
            Assert.DoesNotContain(options.ApprovedKeyFilePath, sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("forgecdn.net", sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(rawSha1, sanitized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void PackageIntegrityManifestBindsEveryFileAndDetectsTampering()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Agent"));
            Directory.CreateDirectory(Path.Combine(root, "WebUi"));
            File.WriteAllText(Path.Combine(root, "ChunkPilot.exe"), "app");
            File.WriteAllText(Path.Combine(root, "ChunkPilot.FirewallHelper.exe"), "firewall");
            File.WriteAllText(Path.Combine(root, "Agent", "ChunkPilot.Agent.exe"), "agent");
            var webUi = Path.Combine(root, "WebUi", "index.html");
            File.WriteAllText(webUi, "webui");
            var head = new string('a', 40);
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    relativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
                    sizeBytes = new FileInfo(path).Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                }).ToArray();
            File.WriteAllText(
                Path.Combine(root, CertificationPackageFreshness.IntegrityManifestFileName),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 3,
                    gitSha = head,
                    packageSourceKind = "isolated-head-archive",
                    packageInputsMatchHead = true,
                    packageInputFileCount = 1,
                    packageInputHeadSha256 = new string('b', 64),
                    packageInputWorktreeSha256 = new string('b', 64),
                    packageInputIndexSha256 = new string('b', 64),
                    files
                }));

            CertificationPackageFreshness.ValidateIntegrityManifest(root, head);
            File.AppendAllText(webUi, "tampered");
            Assert.Throws<InvalidDataException>(() =>
                CertificationPackageFreshness.ValidateIntegrityManifest(root, head));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void PackageManifestRejectsDirtyBuildEvidenceAfterWorktreeIsReverted()
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            RunGit(repository, "init", "--quiet");
            Directory.CreateDirectory(Path.Combine(repository, "src"));
            const string original = "internal sealed class Candidate {}\n";
            var source = Path.Combine(repository, "src", "Candidate.cs");
            File.WriteAllText(source, original);
            RunGit(repository, "add", "src/Candidate.cs");
            RunGit(repository, "-c", "user.name=ChunkPilot Tests",
                "-c", "user.email=chunkpilot-tests@example.invalid",
                "commit", "--quiet", "-m", "fixture");
            var head = RunGitOutput(repository, "rev-parse", "HEAD");
            File.AppendAllText(source, "// dirty build input\n");
            var dirtyBuildProof = CertificationPackageFreshness.ComputePackageInputProof(repository);
            Assert.False(dirtyBuildProof.MatchesHead);

            var package = Path.Combine(root, "package");
            Directory.CreateDirectory(package);
            foreach (var name in new[] { "one.bin", "two.bin", "three.bin" })
                File.WriteAllText(Path.Combine(package, name), name);
            var files = Directory.EnumerateFiles(package).Select(path => new
            {
                relativePath = Path.GetFileName(path),
                sizeBytes = new FileInfo(path).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            }).ToArray();
            File.WriteAllText(
                Path.Combine(package, CertificationPackageFreshness.IntegrityManifestFileName),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 3,
                    gitSha = head,
                    packageSourceKind = "worktree",
                    packageInputsMatchHead = dirtyBuildProof.MatchesHead,
                    packageInputFileCount = dirtyBuildProof.FileCount,
                    packageInputHeadSha256 = dirtyBuildProof.HeadSha256,
                    packageInputWorktreeSha256 = dirtyBuildProof.WorktreeSha256,
                    packageInputIndexSha256 = dirtyBuildProof.IndexSha256,
                    files
                }));

            File.WriteAllText(source, original);
            var currentCleanProof = CertificationPackageFreshness.ComputePackageInputProof(repository);
            Assert.True(currentCleanProof.MatchesHead);
            Assert.Throws<InvalidDataException>(() =>
                CertificationPackageFreshness.ValidateIntegrityManifest(
                    package, head, currentCleanProof));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void CertificationOptionsRefuseStalePreexistingRunDataBeforePackageOrCredentialWork()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "ChunkPilot.sln"), "fixture");
            var runtime = Path.Combine(root, "artifacts", "runtime");
            OwnedCertificationRuntime.Prepare(root, runtime);
            var runRoot = Path.Combine(runtime, "runs", "run-stale");
            var data = Path.Combine(runRoot, "data");
            var servers = Path.Combine(runRoot, "servers");
            var temporary = Path.Combine(runRoot, "temp");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(temporary);
            var options = Options(root, CurseForgeRuntimeCertificationPhase.Metadata) with
            {
                RuntimeRoot = runtime,
                DataRoot = data,
                ManagedServersRoot = servers,
                TemporaryRoot = temporary,
                PayloadLedgerPath = Path.Combine(runtime, "evidence", "ledger.json")
            };

            var exception = Assert.Throws<InvalidOperationException>(options.Validate);
            Assert.Contains("fresh task-owned", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task RecoverableCleanupMovesOnlyExactRunAndRetainsCampaignEvidence()
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            var runtime = OwnedCertificationRuntime.Prepare(
                repository, Path.Combine(repository, "artifacts", "runtime"));
            var runRoot = Path.Combine(runtime, "runs", "run-cleanup");
            var data = Path.Combine(runRoot, "data");
            var servers = Path.Combine(runRoot, "servers");
            var temporary = Path.Combine(runRoot, "temp");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(servers);
            Directory.CreateDirectory(temporary);
            await File.WriteAllTextAsync(Path.Combine(data, "state.db"), "state");
            await File.WriteAllTextAsync(Path.Combine(servers, "server.jar"), "server");
            var ledger = Path.Combine(runtime, "evidence", "curseforge-payload-ledger.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
            await File.WriteAllTextAsync(ledger, "campaign evidence");
            var fakeRecycle = new RecordingRecycleBin(Path.Combine(root, "fake-recycle"));
            var options = Options(repository, CurseForgeRuntimeCertificationPhase.Metadata) with
            {
                RuntimeRoot = runtime,
                DataRoot = data,
                ManagedServersRoot = servers,
                TemporaryRoot = temporary,
                PayloadLedgerPath = ledger
            };

            var evidence = await OwnedCertificationRunCleanup.MoveToRecycleBinAsync(
                options, fakeRecycle, CancellationToken.None);

            Assert.True(evidence.Success);
            Assert.True(evidence.ExactOwnedRunProven);
            Assert.True(evidence.SourceRunRootAbsent);
            Assert.Equal(11, evidence.BytesMovedToRecycleBin);
            Assert.Equal(2, evidence.FilesMovedToRecycleBin);
            Assert.True(fakeRecycle.Called);
            Assert.True(File.Exists(ledger));
            Assert.True(File.Exists(Path.Combine(root, "fake-recycle", "data", "state.db")));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task RecoverableCleanupRejectsMissingRecycleProofEvenWhenSourceVanishes()
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            var runtime = OwnedCertificationRuntime.Prepare(
                repository, Path.Combine(repository, "artifacts", "runtime"));
            var runRoot = Path.Combine(runtime, "runs", "run-unrecoverable-delete");
            var data = Path.Combine(runRoot, "data");
            var servers = Path.Combine(runRoot, "servers");
            var temporary = Path.Combine(runRoot, "temp");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(servers);
            Directory.CreateDirectory(temporary);
            await File.WriteAllTextAsync(Path.Combine(data, "state.db"), "state");
            var fakeRecycle = new RecordingRecycleBin(
                Path.Combine(root, "fake-unrecoverable-destination"),
                recycleConfirmed: false);
            var options = Options(repository, CurseForgeRuntimeCertificationPhase.Metadata) with
            {
                RuntimeRoot = runtime,
                DataRoot = data,
                ManagedServersRoot = servers,
                TemporaryRoot = temporary,
                PayloadLedgerPath = Path.Combine(
                    runtime, "evidence", "curseforge-payload-ledger.json")
            };

            var evidence = await OwnedCertificationRunCleanup.MoveToRecycleBinAsync(
                options, fakeRecycle, CancellationToken.None);

            Assert.True(fakeRecycle.Called);
            Assert.True(evidence.Attempted);
            Assert.True(evidence.ExactOwnedRunProven);
            Assert.True(evidence.SourceRunRootAbsent);
            Assert.False(evidence.Success);
            Assert.Equal(0, evidence.BytesMovedToRecycleBin);
            Assert.Equal(0, evidence.FilesMovedToRecycleBin);
            Assert.Equal(0, evidence.DirectoriesMovedToRecycleBin);
            Assert.Contains(
                "without returning a recoverable Recycle Bin item",
                evidence.Outcome,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task RecoverableCleanupRetainsRunWithUnexpectedSibling()
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            var runtime = OwnedCertificationRuntime.Prepare(
                repository, Path.Combine(repository, "artifacts", "runtime"));
            var runRoot = Path.Combine(runtime, "runs", "run-refused");
            var data = Path.Combine(runRoot, "data");
            var servers = Path.Combine(runRoot, "servers");
            var temporary = Path.Combine(runRoot, "temp");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(servers);
            Directory.CreateDirectory(temporary);
            await File.WriteAllTextAsync(Path.Combine(runRoot, "unexpected.txt"), "retain");
            var fakeRecycle = new RecordingRecycleBin(Path.Combine(root, "fake-recycle"));
            var options = Options(repository, CurseForgeRuntimeCertificationPhase.Metadata) with
            {
                RuntimeRoot = runtime,
                DataRoot = data,
                ManagedServersRoot = servers,
                TemporaryRoot = temporary,
                PayloadLedgerPath = Path.Combine(
                    runtime, "evidence", "curseforge-payload-ledger.json")
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OwnedCertificationRunCleanup.MoveToRecycleBinAsync(
                    options, fakeRecycle, CancellationToken.None));

            Assert.False(fakeRecycle.Called);
            Assert.True(Directory.Exists(runRoot));
            Assert.True(File.Exists(Path.Combine(runRoot, "unexpected.txt")));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task RecoverableCleanupRefusesReparsePointTemporaryTree()
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            var runtime = OwnedCertificationRuntime.Prepare(
                repository, Path.Combine(repository, "artifacts", "runtime"));
            var runRoot = Path.Combine(runtime, "runs", "run-reparse-refused");
            var data = Path.Combine(runRoot, "data");
            var servers = Path.Combine(runRoot, "servers");
            var temporary = Path.Combine(runRoot, "temp");
            var foreign = Path.Combine(root, "foreign-temp");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(servers);
            Directory.CreateDirectory(foreign);
            try
            {
                Directory.CreateSymbolicLink(temporary, foreign);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return;
            }
            var fakeRecycle = new RecordingRecycleBin(Path.Combine(root, "fake-recycle"));
            var options = Options(repository, CurseForgeRuntimeCertificationPhase.Metadata) with
            {
                RuntimeRoot = runtime,
                DataRoot = data,
                ManagedServersRoot = servers,
                TemporaryRoot = temporary,
                PayloadLedgerPath = Path.Combine(
                    runtime, "evidence", "curseforge-payload-ledger.json")
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OwnedCertificationRunCleanup.MoveToRecycleBinAsync(
                    options, fakeRecycle, CancellationToken.None));

            Assert.False(fakeRecycle.Called);
            Assert.True(Directory.Exists(foreign));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void FailedOrCancelledRunRecyclesOnlyAfterEveryStartedJobAndPortAreGone()
    {
        var failed = new CurseForgeRuntimeCertificationReport
        {
            Result = "FAILED",
            BootstrapAgentProcessId = 101,
            BootstrapAgentExited = true,
            AgentProcessId = 202,
            AgentExited = true
        };
        Assert.True(CurseForgeRuntimeCertificationController
            .CanRecycleFreshRunAfterStoppedJobs(failed, selectedPortAbsent: true));
        Assert.False(CurseForgeRuntimeCertificationController
            .CanRecycleFreshRunAfterStoppedJobs(WithAgentExited(false), selectedPortAbsent: true));
        Assert.False(CurseForgeRuntimeCertificationController
            .CanRecycleFreshRunAfterStoppedJobs(failed, selectedPortAbsent: false));

        static CurseForgeRuntimeCertificationReport WithAgentExited(bool exited) => new()
        {
            Result = "CANCELLED",
            BootstrapAgentProcessId = 101,
            BootstrapAgentExited = true,
            AgentProcessId = 202,
            AgentExited = exited
        };
    }

    [Fact]
    public void TaskServerCleanupRequiresEveryApplicablePostcondition()
    {
        var passed = new CertificationCleanupPostconditionsEvidence
        {
            TaskServerInactive = true,
            TaskServerProcessIdentityCaptured = true,
            TaskServerRootProcessExited = true,
            PortListenerAbsent = true,
            NoPartialArtifacts = true,
            NoUnsafeStagingResidue = true,
            ExactOwnedAgentTreeExited = true,
            ExactOwnedAgentExitCodeZero = true,
            ListenerPidOwnershipVerified = true
        };

        Assert.True(CurseForgeRuntimeCertificationSession.TaskServerCleanupPassed(passed));
        Assert.False(CurseForgeRuntimeCertificationSession.TaskServerCleanupPassed(null));
        Assert.False(CurseForgeRuntimeCertificationSession.TaskServerCleanupPassed(
            passed with { PortListenerAbsent = false }));
    }

    [Fact]
    public async Task FailureBeforeServerMaterializationStillReportsExactAgentAndRunCleanup()
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            File.WriteAllText(Path.Combine(repository, "ChunkPilot.sln"), "fixture");
            var runtime = OwnedCertificationRuntime.Prepare(
                repository, Path.Combine(repository, "artifacts", "runtime"));
            var runRoot = Path.Combine(runtime, "runs", "run-early-provider-failure");
            var temporary = Path.Combine(runRoot, "temp");
            Directory.CreateDirectory(temporary);
            var agent = Path.Combine(root, "ChunkPilot.Agent.exe");
            File.WriteAllBytes(agent, []);
            var key = Path.Combine(root, "approved.key");
            File.WriteAllText(key, "not-a-real-key");
            var reportPath = Path.Combine(
                runtime, "evidence", "certification-early-provider-failure.json");
            var pendingPath = Path.Combine(
                runtime, "evidence", "certification-early-provider-failure.pending.json");
            var recycleBin = new RecordingRecycleBin(Path.Combine(root, "fake-recycle"));
            var options = Options(repository, CurseForgeRuntimeCertificationPhase.Full) with
            {
                RuntimeRoot = runtime,
                DataRoot = Path.Combine(runRoot, "data"),
                ManagedServersRoot = Path.Combine(runRoot, "servers"),
                TemporaryRoot = temporary,
                AgentExecutablePath = agent,
                ApprovedKeyFilePath = key,
                PayloadLedgerPath = Path.Combine(
                    runtime, "evidence", "curseforge-payload-ledger.json")
            };
            var controller = new CurseForgeRuntimeCertificationController(
                recycleBin: recycleBin,
                operations: new CurseForgeRuntimeCertificationControllerOperations
                {
                    ValidateOptions = static _ => { },
                    ExecuteAgentCampaignAsync = static (_, report, _) =>
                    {
                        report.BootstrapCleanupSucceeded = true;
                        report.CertifiedCleanupSucceeded = true;
                        report.DpapiRelaunchAuthenticatedCatalogResolve = true;
                        throw new InvalidDataException(
                            "injected failure before server materialization");
                    },
                    ValidateFinalPackageFreshness = static _ => { }
                },
                evidenceJournal: new CertificationEvidenceJournal(reportPath, pendingPath));

            var report = await controller.RunAsync(options, CancellationToken.None);

            Assert.False(report.Success);
            Assert.Equal("FAILED", report.Result);
            Assert.Contains("before server materialization", report.Error, StringComparison.Ordinal);
            Assert.Null(report.ServerId);
            Assert.False(report.CertifiedTaskServerCleanupApplicable);
            Assert.False(report.CertifiedTaskServerCleanupSucceeded);
            Assert.True(report.CertifiedCleanupSucceeded);
            Assert.True(report.FreshRunRecycle.Success);
            Assert.True(report.CleanupSucceeded);
            Assert.False(Directory.Exists(runRoot));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task FinalFreshnessFailureCannotReportPassedAndStillAttemptsSafeRunCleanup()
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            File.WriteAllText(Path.Combine(repository, "ChunkPilot.sln"), "fixture");
            var runtime = OwnedCertificationRuntime.Prepare(
                repository, Path.Combine(repository, "artifacts", "runtime"));
            var runRoot = Path.Combine(runtime, "runs", "run-final-freshness-failure");
            var temporary = Path.Combine(runRoot, "temp");
            Directory.CreateDirectory(temporary);
            var agent = Path.Combine(root, "ChunkPilot.Agent.exe");
            File.WriteAllBytes(agent, []);
            var key = Path.Combine(root, "approved.key");
            File.WriteAllText(key, "not-a-real-key");
            var reportPath = Path.Combine(
                runtime, "evidence", "certification-final-freshness-failure.json");
            var pendingPath = Path.Combine(
                runtime, "evidence", "certification-final-freshness-failure.pending.json");
            var journal = new CertificationEvidenceJournal(reportPath, pendingPath);
            var recycleBin = new RecordingRecycleBin(
                Path.Combine(root, "fake-recycle"), pendingPath);
            var options = Options(repository, CurseForgeRuntimeCertificationPhase.Metadata) with
            {
                RuntimeRoot = runtime,
                DataRoot = Path.Combine(runRoot, "data"),
                ManagedServersRoot = Path.Combine(runRoot, "servers"),
                TemporaryRoot = temporary,
                AgentExecutablePath = agent,
                ApprovedKeyFilePath = key,
                PayloadLedgerPath = Path.Combine(
                    runtime, "evidence", "curseforge-payload-ledger.json")
            };
            var controller = new CurseForgeRuntimeCertificationController(
                recycleBin: recycleBin,
                operations: new CurseForgeRuntimeCertificationControllerOperations
                {
                    ValidateOptions = static _ => { },
                    ExecuteAgentCampaignAsync = static (_, report, _) =>
                    {
                        report.BootstrapCleanupSucceeded = true;
                        report.CertifiedCleanupSucceeded = true;
                        report.DpapiRelaunchAuthenticatedCatalogResolve = true;
                        report.Success = true;
                        return Task.CompletedTask;
                    },
                    ValidateFinalPackageFreshness = static _ =>
                        throw new InvalidDataException("injected final freshness failure")
                },
                evidenceJournal: journal);

            var report = await controller.RunAsync(options, CancellationToken.None);

            Assert.False(report.Success);
            Assert.Equal("FAILED", report.Result);
            Assert.Contains("injected final freshness failure", report.Error, StringComparison.Ordinal);
            Assert.True(report.FreshRunRecycle.Attempted);
            Assert.True(report.FreshRunRecycle.Success);
            Assert.True(recycleBin.Called);
            Assert.True(recycleBin.PendingObservedBeforeMove);
            Assert.True(File.Exists(pendingPath));
            Assert.False(Directory.Exists(runRoot));
            await journal.FinalizeAsync(report, CancellationToken.None);
            Assert.True(File.Exists(reportPath));
            Assert.False(File.Exists(pendingPath));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task CleanupFailureRetainsRunAndFinalizesFailureEvidence()
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            File.WriteAllText(Path.Combine(repository, "ChunkPilot.sln"), "fixture");
            var runtime = OwnedCertificationRuntime.Prepare(
                repository, Path.Combine(repository, "artifacts", "runtime"));
            var runRoot = Path.Combine(runtime, "runs", "run-cleanup-failure");
            var temporary = Path.Combine(runRoot, "temp");
            Directory.CreateDirectory(temporary);
            var agent = Path.Combine(root, "ChunkPilot.Agent.exe");
            File.WriteAllBytes(agent, []);
            var key = Path.Combine(root, "approved.key");
            File.WriteAllText(key, "not-a-real-key");
            var reportPath = Path.Combine(
                runtime, "evidence", "certification-cleanup-failure.json");
            var pendingPath = Path.Combine(
                runtime, "evidence", "certification-cleanup-failure.pending.json");
            var journal = new CertificationEvidenceJournal(reportPath, pendingPath);
            var recycleBin = new NoOpRecycleBin();
            var options = Options(repository, CurseForgeRuntimeCertificationPhase.Metadata) with
            {
                RuntimeRoot = runtime,
                DataRoot = Path.Combine(runRoot, "data"),
                ManagedServersRoot = Path.Combine(runRoot, "servers"),
                TemporaryRoot = temporary,
                AgentExecutablePath = agent,
                ApprovedKeyFilePath = key,
                PayloadLedgerPath = Path.Combine(
                    runtime, "evidence", "curseforge-payload-ledger.json")
            };
            var controller = new CurseForgeRuntimeCertificationController(
                recycleBin: recycleBin,
                operations: SuccessfulSyntheticCampaign(),
                evidenceJournal: journal);

            var report = await controller.RunAsync(options, CancellationToken.None);

            Assert.False(report.Success);
            Assert.Equal("FAILED", report.Result);
            Assert.True(report.FreshRunRecycle.Attempted);
            Assert.False(report.FreshRunRecycle.Success);
            Assert.True(recycleBin.Called);
            Assert.True(Directory.Exists(runRoot));
            Assert.True(File.Exists(pendingPath));
            await journal.FinalizeAsync(report, CancellationToken.None);
            Assert.True(File.Exists(reportPath));
            Assert.False(File.Exists(pendingPath));
            Assert.True(Directory.Exists(runRoot));
        }
        finally
        {
            DeleteTree(root);
        }

        static CurseForgeRuntimeCertificationControllerOperations SuccessfulSyntheticCampaign() =>
            new()
            {
                ValidateOptions = static _ => { },
                ExecuteAgentCampaignAsync = static (_, report, _) =>
                {
                    report.BootstrapCleanupSucceeded = true;
                    report.CertifiedCleanupSucceeded = true;
                    report.DpapiRelaunchAuthenticatedCatalogResolve = true;
                    report.Success = true;
                    return Task.CompletedTask;
                },
                ValidateFinalPackageFreshness = static _ => { }
            };
    }

    [Fact]
    public void EvidencePathsRefuseAliasesReservedLocationsAndOverwrite()
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            var runtime = OwnedCertificationRuntime.Prepare(
                repository, Path.Combine(repository, "artifacts", "runtime"));
            var safe = CurseForgeRuntimeCertificationCommand.ResolveEvidencePaths(
                runtime, "run-safe", requestedReport: null, payloadLedgerOverridden: false);
            Assert.Equal(Path.Combine(runtime, "evidence"),
                Path.GetDirectoryName(safe.ReportPath));
            Assert.NotEqual(safe.ReportPath, safe.PayloadLedgerPath);
            Assert.NotEqual(safe.ReportPath, safe.PendingPath);
            Assert.EndsWith(".pending.json", safe.PendingPath, StringComparison.Ordinal);

            Assert.Throws<ArgumentException>(() =>
                CurseForgeRuntimeCertificationCommand.ResolveEvidencePaths(
                    runtime, "run-safe", safe.PayloadLedgerPath, false));
            Assert.Throws<ArgumentException>(() =>
                CurseForgeRuntimeCertificationCommand.ResolveEvidencePaths(
                    runtime, "run-safe", Path.Combine(runtime, OwnedCertificationRuntime.MarkerFileName), false));
            Assert.Throws<ArgumentException>(() =>
                CurseForgeRuntimeCertificationCommand.ResolveEvidencePaths(
                    runtime, "run-safe", null, payloadLedgerOverridden: true));

            var existing = Path.Combine(runtime, "evidence", "certification-existing.json");
            File.WriteAllText(existing, "do not overwrite");
            Assert.Throws<ArgumentException>(() =>
                CurseForgeRuntimeCertificationCommand.ResolveEvidencePaths(
                    runtime, "run-safe", existing, false));
            Assert.Equal("do not overwrite", File.ReadAllText(existing));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task EvidenceJournalDurablyStagesThenAtomicallyFinalizes()
    {
        var root = NewRoot();
        try
        {
            var evidence = Path.Combine(root, "evidence");
            Directory.CreateDirectory(evidence);
            var reportPath = Path.Combine(evidence, "certification-run.json");
            var pendingPath = Path.Combine(evidence, "certification-run.pending.json");
            var journal = new CertificationEvidenceJournal(reportPath, pendingPath);
            var report = new CurseForgeRuntimeCertificationReport
            {
                RunId = "run",
                Result = "FAILED",
                Error = "synthetic",
                CompletedAtUtc = DateTimeOffset.UtcNow
            };

            await journal.WritePendingAsync(report, CancellationToken.None);
            Assert.True(File.Exists(pendingPath));
            Assert.False(File.Exists(reportPath));
            using (var pending = JsonDocument.Parse(await File.ReadAllTextAsync(pendingPath)))
            {
                Assert.Equal(
                    CurseForgeRuntimeCertificationPendingEvidence.ExpectedDocumentType,
                    pending.RootElement.GetProperty("documentType").GetString());
                Assert.Equal(
                    "Packaged Agent named-pipe protocol",
                    pending.RootElement.GetProperty("report")
                        .GetProperty("certificationInterface").GetString());
                Assert.False(pending.RootElement.GetProperty("report")
                    .GetProperty("packagedWebUiExercised").GetBoolean());
            }

            await journal.FinalizeAsync(report, CancellationToken.None);
            Assert.True(File.Exists(reportPath));
            Assert.False(File.Exists(pendingPath));
            using var finalized = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            Assert.Equal(
                "ChunkPilot.CurseForgeRuntimeCertificationReport",
                finalized.RootElement.GetProperty("documentType").GetString());
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task EvidenceJournalRetainsPendingEvidenceWhenFinalTargetIsRaced()
    {
        var root = NewRoot();
        try
        {
            var evidence = Path.Combine(root, "evidence");
            Directory.CreateDirectory(evidence);
            var reportPath = Path.Combine(evidence, "certification-run.json");
            var pendingPath = Path.Combine(evidence, "certification-run.pending.json");
            var journal = new CertificationEvidenceJournal(reportPath, pendingPath);
            var report = new CurseForgeRuntimeCertificationReport
            {
                RunId = "run",
                Result = "FAILED",
                Error = "synthetic",
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            await journal.WritePendingAsync(report, CancellationToken.None);
            await File.WriteAllTextAsync(reportPath, "foreign");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                journal.FinalizeAsync(report, CancellationToken.None));

            Assert.True(File.Exists(pendingPath));
            Assert.Equal("foreign", await File.ReadAllTextAsync(reportPath));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task PayloadLedgerRejectsCertificationReportDocumentType()
    {
        var root = NewRoot();
        try
        {
            var ledger = Path.Combine(root, "ledger.json");
            await File.WriteAllTextAsync(
                ledger, JsonSerializer.Serialize(new CurseForgeRuntimeCertificationReport()));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new CurseForgePayloadBudget(ledger).GetSnapshotAsync(CancellationToken.None));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void ListenerOwnerRequiresLiveStableCreationOrderedTaskServerAncestry()
    {
        var parents = new Dictionary<int, int>
        {
            [200] = 100,
            [201] = 200,
            [202] = 201,
            [300] = 100
        };
        var identities = new Dictionary<int, long>
        {
            [200] = 500,
            [201] = 600,
            [202] = 700,
            [300] = 550
        };
        bool StillMatches(int processId, long creationTicks) =>
            identities.TryGetValue(processId, out var expected) && expected == creationTicks;

        Assert.True(WindowsProcessParents.IsLiveStableRootOrDescendant(
            202, 200, parents, identities, StillMatches));
        Assert.True(WindowsProcessParents.IsLiveStableRootOrDescendant(
            200, 200, new Dictionary<int, int>(), identities, StillMatches));
        Assert.False(WindowsProcessParents.IsLiveStableRootOrDescendant(
            300, 200, parents, identities, StillMatches));
        Assert.False(WindowsProcessParents.IsLiveStableRootOrDescendant(
            100, 200, parents, identities, StillMatches));
    }

    [Fact]
    public void ListenerOwnerRejectsMissingExitedOrPidReusedIntermediateParent()
    {
        var parents = new Dictionary<int, int>
        {
            [202] = 201,
            [201] = 200
        };
        var identities = new Dictionary<int, long>
        {
            [200] = 500,
            [201] = 600,
            [202] = 700
        };

        Assert.False(WindowsProcessParents.IsLiveStableRootOrDescendant(
            202,
            200,
            parents,
            new Dictionary<int, long> { [200] = 500, [202] = 700 },
            (_, _) => true));
        Assert.False(WindowsProcessParents.IsLiveStableRootOrDescendant(
            202,
            200,
            parents,
            identities,
            (processId, _) => processId != 201));

        var reusedParentIdentities = new Dictionary<int, long>(identities)
        {
            [201] = 800
        };
        Assert.False(WindowsProcessParents.IsLiveStableRootOrDescendant(
            202, 200, parents, reusedParentIdentities, (_, _) => true));
    }

    [Fact]
    public void NativeTcpEndpointParserPreservesIpv4LoopbackAndWildcardAddresses()
    {
        var loopback = Assert.Single(WindowsOwnedNetworkEndpoints.ParseTcpTable(
            NativeTcpTable(IPAddress.Loopback, 25_585, 200),
            AddressFamily.InterNetwork));
        var wildcard = Assert.Single(WindowsOwnedNetworkEndpoints.ParseTcpTable(
            NativeTcpTable(IPAddress.Any, 25_586, 201),
            AddressFamily.InterNetwork));

        Assert.Equal(WindowsOwnedEndpointTransport.Tcp, loopback.Transport);
        Assert.Equal(AddressFamily.InterNetwork, loopback.AddressFamily);
        Assert.Equal(IPAddress.Loopback, loopback.LocalAddress);
        Assert.True(loopback.IsExactLoopback);
        Assert.Equal(25_585, loopback.Port);
        Assert.Equal(200, loopback.ProcessId);
        Assert.Equal(IPAddress.Any, wildcard.LocalAddress);
        Assert.True(wildcard.IsWildcard);
        Assert.False(wildcard.IsExactLoopback);
        Assert.Equal(25_586, wildcard.Port);
        Assert.Equal(201, wildcard.ProcessId);
    }

    [Fact]
    public void NativeTcpEndpointParserPreservesIpv6LoopbackAndWildcardAddresses()
    {
        var loopback = Assert.Single(WindowsOwnedNetworkEndpoints.ParseTcpTable(
            NativeTcpTable(IPAddress.IPv6Loopback, 25_585, 200),
            AddressFamily.InterNetworkV6));
        var wildcard = Assert.Single(WindowsOwnedNetworkEndpoints.ParseTcpTable(
            NativeTcpTable(IPAddress.IPv6Any, 25_586, 201),
            AddressFamily.InterNetworkV6));

        Assert.Equal(WindowsOwnedEndpointTransport.Tcp, loopback.Transport);
        Assert.Equal(AddressFamily.InterNetworkV6, loopback.AddressFamily);
        Assert.Equal(IPAddress.IPv6Loopback, loopback.LocalAddress);
        Assert.True(loopback.IsExactLoopback);
        Assert.Equal(25_585, loopback.Port);
        Assert.Equal(200, loopback.ProcessId);
        Assert.Equal(IPAddress.IPv6Any, wildcard.LocalAddress);
        Assert.True(wildcard.IsWildcard);
        Assert.False(wildcard.IsExactLoopback);
        Assert.Equal(25_586, wildcard.Port);
        Assert.Equal(201, wildcard.ProcessId);
    }

    [Fact]
    public void NativeUdpEndpointParserPreservesIpv4AndIpv6Endpoints()
    {
        var ipv4 = Assert.Single(WindowsOwnedNetworkEndpoints.ParseUdpTable(
            NativeUdpTable(IPAddress.Loopback, 25_585, 200),
            AddressFamily.InterNetwork));
        var ipv6 = Assert.Single(WindowsOwnedNetworkEndpoints.ParseUdpTable(
            NativeUdpTable(IPAddress.IPv6Any, 25_586, 201),
            AddressFamily.InterNetworkV6));

        Assert.Equal(WindowsOwnedEndpointTransport.Udp, ipv4.Transport);
        Assert.Equal(AddressFamily.InterNetwork, ipv4.AddressFamily);
        Assert.Equal(IPAddress.Loopback, ipv4.LocalAddress);
        Assert.Equal(25_585, ipv4.Port);
        Assert.Equal(200, ipv4.ProcessId);
        Assert.Equal(WindowsOwnedEndpointTransport.Udp, ipv6.Transport);
        Assert.Equal(AddressFamily.InterNetworkV6, ipv6.AddressFamily);
        Assert.Equal(IPAddress.IPv6Any, ipv6.LocalAddress);
        Assert.Equal(25_586, ipv6.Port);
        Assert.Equal(201, ipv6.ProcessId);
    }

    [Fact]
    public void OwnedEndpointPolicyAcceptsOnlySelectedPortTcpOnIpv4OrIpv6Loopback()
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var result = EvaluateOwnedEndpoints(
                new WindowsOwnedNetworkEndpoint(
                    WindowsOwnedEndpointTransport.Tcp,
                    address.AddressFamily,
                    address,
                    25_585,
                    200));

            Assert.True(result.Passed);
            Assert.True(result.OwnershipVerified);
            Assert.Equal(1, result.ExpectedMinecraftListenerCount);
            Assert.Equal(1, result.ObservedOwnedEndpointCount);
            Assert.Equal(0, result.UnexpectedOwnedEndpointCount);
        }
    }

    [Fact]
    public void OwnedEndpointPolicyRejectsIpv4AndIpv6WildcardListeners()
    {
        foreach (var wildcard in new[] { IPAddress.Any, IPAddress.IPv6Any })
        {
            var result = EvaluateOwnedEndpoints(
                ApprovedMinecraftEndpoint(),
                new WindowsOwnedNetworkEndpoint(
                    WindowsOwnedEndpointTransport.Tcp,
                    wildcard.AddressFamily,
                    wildcard,
                    25_585,
                    200));

            Assert.False(result.Passed);
            Assert.True(result.OwnershipVerified);
            Assert.Equal(1, result.UnexpectedOwnedEndpointCount);
            Assert.Equal(1, result.ObservedOwnedWildcardEndpointCount);
            Assert.Equal(1, result.ObservedOwnedNonLoopbackEndpointCount);
        }
    }

    [Fact]
    public void OwnedEndpointPolicyRejectsUdpAndSecondaryLoopbackPorts()
    {
        var result = EvaluateOwnedEndpoints(
            ApprovedMinecraftEndpoint(),
            new WindowsOwnedNetworkEndpoint(
                WindowsOwnedEndpointTransport.Udp,
                AddressFamily.InterNetwork,
                IPAddress.Loopback,
                25_585,
                200),
            new WindowsOwnedNetworkEndpoint(
                WindowsOwnedEndpointTransport.Tcp,
                AddressFamily.InterNetworkV6,
                IPAddress.IPv6Loopback,
                25_586,
                200));

        Assert.False(result.Passed);
        Assert.True(result.OwnershipVerified);
        Assert.Equal(2, result.ObservedOwnedTcpListenerCount);
        Assert.Equal(1, result.ObservedOwnedUdpEndpointCount);
        Assert.Equal(2, result.UnexpectedOwnedEndpointCount);
        Assert.Equal(0, result.ObservedOwnedNonLoopbackEndpointCount);
    }

    [Fact]
    public void OwnedEndpointPolicyIgnoresEndpointsFromPidsOutsideExactJob()
    {
        var result = EvaluateOwnedEndpoints(
            ApprovedMinecraftEndpoint(),
            new WindowsOwnedNetworkEndpoint(
                WindowsOwnedEndpointTransport.Tcp,
                AddressFamily.InterNetworkV6,
                IPAddress.IPv6Any,
                44_444,
                999));

        Assert.True(result.Passed);
        Assert.Equal(1, result.ObservedOwnedEndpointCount);
        Assert.Equal(0, result.UnexpectedOwnedEndpointCount);
    }

    [Fact]
    public void OwnedEndpointControllerBracketsTablesWithExactJobIdentitySnapshots()
    {
        var order = new List<string>();
        var identityCaptureCount = 0;
        var endpointCaptureCount = 0;
        var source = new DelegateEndpointSource(() =>
        {
            endpointCaptureCount++;
            Assert.Equal(endpointCaptureCount, identityCaptureCount);
            order.Add(endpointCaptureCount == 1 ? "endpoints-first" : "endpoints-second");
            return
            [
                new WindowsOwnedNetworkEndpoint(
                    WindowsOwnedEndpointTransport.Tcp,
                    AddressFamily.InterNetwork,
                    IPAddress.Loopback,
                    25_585,
                    201)
            ];
        });
        var identities = new Dictionary<int, long>
        {
            [200] = 500,
            [201] = 501
        };

        var result = CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
            source,
            25_585,
            200,
            500,
            () =>
            {
                identityCaptureCount++;
                order.Add(identityCaptureCount switch
                {
                    1 => "job-before",
                    2 => "job-between",
                    _ => "job-after"
                });
                return JobSnapshot(identities);
            },
            () =>
            {
                Assert.Equal(3, identityCaptureCount);
                order.Add("parents");
                return new Dictionary<int, int> { [201] = 200 };
            },
            (processId, identity) =>
                identities.TryGetValue(processId, out var expected) && expected == identity);

        Assert.True(result.Passed);
        Assert.Equal(1, result.ExpectedMinecraftListenerCount);
        Assert.Equal(
            "job-before|endpoints-first|job-between|endpoints-second|job-after|parents",
            string.Join('|', order));
    }

    [Fact]
    public void OwnedEndpointPolicyRejectsStableProcessSocketMutationBetweenInventories()
    {
        var endpointCaptureCount = 0;
        var identities = new Dictionary<int, long> { [200] = 500 };
        var result = CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
            new DelegateEndpointSource(() =>
            {
                endpointCaptureCount++;
                return endpointCaptureCount == 1
                    ? [ApprovedMinecraftEndpoint()]
                    :
                    [
                        ApprovedMinecraftEndpoint(),
                        new WindowsOwnedNetworkEndpoint(
                            WindowsOwnedEndpointTransport.Tcp,
                            AddressFamily.InterNetworkV6,
                            IPAddress.IPv6Any,
                            44_444,
                            200)
                    ];
            }),
            25_585,
            200,
            500,
            () => JobSnapshot(identities),
            () => new Dictionary<int, int>(),
            (processId, identity) => processId == 200 && identity == 500);

        Assert.False(result.Passed);
        Assert.True(result.JobAccountingGenerationStable);
        Assert.True(result.JobProcessIdentitySetStable);
        Assert.False(result.OwnedEndpointInventoriesStable);
        Assert.Equal(1, result.EndpointInventoryDifferenceCount);
    }

    [Fact]
    public void OwnedEndpointPolicyRejectsPostOnlyOrChangedJobProcessCreation()
    {
        var endpoint = new WindowsOwnedNetworkEndpoint(
            WindowsOwnedEndpointTransport.Tcp,
            AddressFamily.InterNetwork,
            IPAddress.Loopback,
            25_585,
            201);
        var postOnlyCapture = 0;
        var postOnly = CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
            new FixedEndpointSource([endpoint]),
            25_585,
            200,
            500,
            () => ++postOnlyCapture == 1
                ? JobSnapshot(new Dictionary<int, long> { [200] = 500 })
                : JobSnapshot(new Dictionary<int, long> { [200] = 500, [201] = 501 }),
            () => new Dictionary<int, int> { [201] = 200 },
            (_, _) => true);
        var changedCreationCapture = 0;
        var changedCreation = CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
            new FixedEndpointSource([endpoint]),
            25_585,
            200,
            500,
            () => ++changedCreationCapture == 1
                ? JobSnapshot(new Dictionary<int, long> { [200] = 500, [201] = 501 })
                : JobSnapshot(new Dictionary<int, long> { [200] = 500, [201] = 502 }),
            () => new Dictionary<int, int> { [201] = 200 },
            (_, _) => true);

        Assert.False(postOnly.Passed);
        Assert.False(postOnly.OwnedEndpointIdentitySnapshotsStable);
        Assert.Equal(2, postOnly.CaptureRaceEndpointCount);
        Assert.False(changedCreation.Passed);
        Assert.False(changedCreation.OwnedEndpointIdentitySnapshotsStable);
        Assert.Equal(2, changedCreation.CaptureRaceEndpointCount);
    }

    [Fact]
    public void OwnedEndpointPolicyRejectsEphemeralJobGenerationAbsentFromBothPidSnapshots()
    {
        var identities = new Dictionary<int, long> { [200] = 500 };
        var captureCount = 0;
        var result = CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
            new FixedEndpointSource(
            [
                ApprovedMinecraftEndpoint(),
                new WindowsOwnedNetworkEndpoint(
                    WindowsOwnedEndpointTransport.Udp,
                    AddressFamily.InterNetworkV6,
                    IPAddress.IPv6Any,
                    44_444,
                    999)
            ]),
            25_585,
            200,
            500,
            () => ++captureCount == 1
                ? JobSnapshot(identities, totalProcesses: 1)
                : JobSnapshot(identities, totalProcesses: 2, totalTerminatedProcesses: 1),
            () => new Dictionary<int, int>(),
            (processId, identity) => processId == 200 && identity == 500);

        Assert.False(result.Passed);
        Assert.False(result.JobAccountingGenerationStable);
        Assert.False(result.JobProcessIdentitySetStable);
        Assert.Equal(0, result.CaptureRaceEndpointCount);
    }

    [Fact]
    public void OwnedEndpointPolicyRequiresLiveCreationIdentityAndTaskServerSubtree()
    {
        var identities = new Dictionary<int, long>
        {
            [200] = 500,
            [201] = 501
        };
        var endpoint = new WindowsOwnedNetworkEndpoint(
            WindowsOwnedEndpointTransport.Tcp,
            AddressFamily.InterNetwork,
            IPAddress.Loopback,
            25_585,
            201);
        var staleIdentity = CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
            new FixedEndpointSource([endpoint]),
            25_585,
            200,
            500,
            identities,
            new Dictionary<int, int> { [201] = 200 },
            (processId, _) => processId == 200);
        var wrongSubtree = CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
            new FixedEndpointSource([endpoint]),
            25_585,
            200,
            500,
            identities,
            new Dictionary<int, int> { [201] = 100 },
            (_, _) => true);

        Assert.False(staleIdentity.Passed);
        Assert.False(staleIdentity.OwnedEndpointProcessIdentitiesVerified);
        Assert.False(wrongSubtree.Passed);
        Assert.False(wrongSubtree.OwnedEndpointsWithinTaskServerSubtree);
    }

    [Fact]
    public void OwnedEndpointPolicyRejectsPidReusedIntermediateAncestryGeneration()
    {
        var identities = new Dictionary<int, long>
        {
            [200] = 500,
            [201] = 800,
            [202] = 700
        };
        var endpoint = new WindowsOwnedNetworkEndpoint(
            WindowsOwnedEndpointTransport.Tcp,
            AddressFamily.InterNetwork,
            IPAddress.Loopback,
            25_585,
            202);

        var result = CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
            new FixedEndpointSource([endpoint]),
            25_585,
            200,
            500,
            identities,
            new Dictionary<int, int> { [202] = 201, [201] = 200 },
            (processId, creationTicks) =>
                identities.TryGetValue(processId, out var expected) &&
                expected == creationTicks);

        Assert.False(result.Passed);
        Assert.True(result.JobProcessIdentitySetStable);
        Assert.True(result.OwnedEndpointProcessIdentitiesVerified);
        Assert.False(result.OwnedEndpointsWithinTaskServerSubtree);
        Assert.Contains("creation-ordered ancestry", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OwnedEndpointPolicyFailsClosedAndSanitizesNativeApiFailure()
    {
        var result = CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
            new FixedEndpointSource(
                [],
                new Win32Exception(5, "synthetic-secret-path should never enter evidence")),
            25_585,
            200,
            500,
            new Dictionary<int, long> { [200] = 500 },
            new Dictionary<int, int>(),
            (_, _) => true);

        Assert.False(result.InventorySucceeded);
        Assert.False(result.Passed);
        Assert.DoesNotContain("synthetic-secret-path", result.Detail, StringComparison.Ordinal);
        Assert.Contains("inventory failed", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WindowsTcpListenerOwnerTableMapsExactLoopbackPortToProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IReadOnlyList<WindowsTcpListenerOwner> owners = [];
        for (var attempt = 0; attempt < 20; attempt++)
        {
            owners = WindowsTcpListenerOwners.ForPort(port);
            if (owners.Any(owner => owner.ProcessId == Environment.ProcessId))
                break;
            await Task.Delay(25);
        }
        Assert.Contains(owners, owner =>
            owner.Port == port && owner.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public async Task WindowsJobStartsProcessAtomicallyOwnedAndProvesEmptyAfterTermination()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var job = WindowsOwnedProcessJob.Create();
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("30");
        start.ArgumentList.Add("127.0.0.1");
        using var process = job.StartSuspendedAssignedAndResume(start);
        var snapshot = job.CaptureStableProcessSnapshot();
        Assert.Contains(process.Id, snapshot.ProcessIdentities.Keys);
        Assert.Equal(checked((uint)snapshot.ProcessIdentities.Count), snapshot.ActiveProcesses);
        Assert.True(snapshot.TotalProcesses >= snapshot.ActiveProcesses);
        job.Terminate();
        Assert.True(await job.WaitForEmptyAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(0, job.ActiveProcessCount);
    }

    [Fact]
    public async Task WindowsJobHandleClosureStopsExactOwnedProcessWithoutExplicitTermination()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var job = WindowsOwnedProcessJob.Create();
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("30");
        start.ArgumentList.Add("127.0.0.1");
        using var process = job.StartSuspendedAssignedAndResume(start);
        Assert.False(process.HasExited);
        job.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.HasExited);
    }

    [Fact]
    public void RealWindowsRecycleAdapterMovesIsolatedTinyDirectoryWhenExplicitlyEnabled()
    {
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(
                Environment.GetEnvironmentVariable("CHUNKPILOT_TEST_REAL_RECYCLE_BIN"),
                "1", StringComparison.Ordinal))
            return;
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "isolated-recycle-adapter-proof");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "proof.txt"), "recoverable");
            var recycled = new SilentWindowsCertificationRecycleBin().MoveDirectory(target);
            Assert.True(recycled);
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    private static CurseForgeRuntimeCertificationOptions Options(
        string root,
        CurseForgeRuntimeCertificationPhase phase) => new()
        {
            RepositoryRoot = root,
            RuntimeRoot = Path.Combine(root, "artifacts", "runtime"),
            ExpectedGitSha = new string('a', 40),
            AgentExecutablePath = Path.Combine(root, "ChunkPilot.Agent.exe"),
            DataRoot = Path.Combine(root, "data"),
            ManagedServersRoot = Path.Combine(root, "servers"),
            TemporaryRoot = Path.Combine(root, "temp"),
            ApprovedKeyFilePath = Path.Combine(root, "approved.key"),
            PayloadLedgerPath = Path.Combine(root, "payload-ledger.json"),
            ProjectReference = "123",
            ClientFileId = "456",
            Phase = phase,
            ExplicitEulaAuthorization = phase == CurseForgeRuntimeCertificationPhase.Official,
            Port = 25_585
        };

    private static CatalogItem Project() => new()
    {
        Provider = CatalogProvider.CurseForge,
        ContentType = CatalogContentType.Modpack,
        ProjectId = "123",
        Slug = "fixture",
        Name = "Fixture",
        Versions =
        [
            new CatalogVersion
            {
                VersionId = "456",
                ClientFileId = "456",
                ServerPackFileId = "789",
                VersionName = "Fixture 1",
                MinecraftVersion = "1.21.1",
                Loader = "NeoForge",
                LoaderVersion = "21.1.1",
                RequiredJavaMajor = 21,
                HasServerPackage = true,
                ClientDownloadUrl = "https://mediafilez.forgecdn.net/files/111/client.zip",
                ClientSha1 = new string('a', 40),
                ClientSizeBytes = 100,
                DownloadUrl = "https://mediafilez.forgecdn.net/files/222/server.zip",
                Sha1 = new string('b', 40),
                SizeBytes = 200
            }
        ]
    };

    private static CurseForgeModpackPreflightResult ReadyPreflight(Guid operationId) => new()
    {
        OperationId = operationId,
        ProjectId = "123",
        ClientFileId = "456",
        ServerPackFileId = "789",
        State = CatalogReleasePreflightState.Ready,
        MinecraftVersion = "1.21.1",
        Loader = "NeoForge",
        LoaderVersion = "21.1.1",
        RequiredJavaMajor = 21,
        ClientDownloadUrl = "https://mediafilez.forgecdn.net/files/111/client.zip",
        ClientSha1 = new string('a', 40),
        ClientSha256 = new string('c', 64),
        ClientSizeBytes = 100,
        ServerPackDownloadUrl = "https://mediafilez.forgecdn.net/files/222/server.zip",
        ServerPackSha1 = new string('b', 40),
        ServerPackSizeBytes = 200
    };

    private static CertificationOwnedEndpointPolicyEvaluation EvaluateOwnedEndpoints(
        params WindowsOwnedNetworkEndpoint[] endpoints)
    {
        var identities = new Dictionary<int, long> { [200] = 500 };
        return CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
            new FixedEndpointSource(endpoints),
            25_585,
            200,
            500,
            identities,
            new Dictionary<int, int>(),
            (processId, identity) =>
                identities.TryGetValue(processId, out var expected) && expected == identity);
    }

    private static WindowsOwnedNetworkEndpoint ApprovedMinecraftEndpoint() =>
        new(
            WindowsOwnedEndpointTransport.Tcp,
            AddressFamily.InterNetwork,
            IPAddress.Loopback,
            25_585,
            200);

    private static WindowsOwnedProcessJobSnapshot JobSnapshot(
        IReadOnlyDictionary<int, long> identities,
        uint? totalProcesses = null,
        uint totalTerminatedProcesses = 0)
    {
        var activeProcesses = checked((uint)identities.Count);
        return new WindowsOwnedProcessJobSnapshot(
            new Dictionary<int, long>(identities),
            totalProcesses ?? activeProcesses,
            activeProcesses,
            totalTerminatedProcesses);
    }

    private static byte[] NativeTcpTable(IPAddress address, int port, int processId)
    {
        var ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        var rowSize = ipv4 ? 24 : 56;
        var table = new byte[sizeof(uint) + rowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0, sizeof(uint)), 1);
        var row = table.AsSpan(sizeof(uint), rowSize);
        if (ipv4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(0, sizeof(uint)), 2);
            address.GetAddressBytes().CopyTo(row.Slice(4, 4));
            WriteNativeNetworkPort(row.Slice(8, sizeof(uint)), port);
            BinaryPrimitives.WriteUInt32LittleEndian(
                row.Slice(20, sizeof(uint)), checked((uint)processId));
        }
        else
        {
            address.GetAddressBytes().CopyTo(row.Slice(0, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(
                row.Slice(16, sizeof(uint)), checked((uint)address.ScopeId));
            WriteNativeNetworkPort(row.Slice(20, sizeof(uint)), port);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(48, sizeof(uint)), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(
                row.Slice(52, sizeof(uint)), checked((uint)processId));
        }
        return table;
    }

    private static byte[] NativeUdpTable(IPAddress address, int port, int processId)
    {
        var ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        var rowSize = ipv4 ? 12 : 28;
        var table = new byte[sizeof(uint) + rowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0, sizeof(uint)), 1);
        var row = table.AsSpan(sizeof(uint), rowSize);
        address.GetAddressBytes().CopyTo(row.Slice(0, ipv4 ? 4 : 16));
        if (ipv4)
        {
            WriteNativeNetworkPort(row.Slice(4, sizeof(uint)), port);
            BinaryPrimitives.WriteUInt32LittleEndian(
                row.Slice(8, sizeof(uint)), checked((uint)processId));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                row.Slice(16, sizeof(uint)), checked((uint)address.ScopeId));
            WriteNativeNetworkPort(row.Slice(20, sizeof(uint)), port);
            BinaryPrimitives.WriteUInt32LittleEndian(
                row.Slice(24, sizeof(uint)), checked((uint)processId));
        }
        return table;
    }

    private static void WriteNativeNetworkPort(Span<byte> field, int port)
    {
        field.Clear();
        field[0] = checked((byte)(port >> 8));
        field[1] = checked((byte)(port & 0xff));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ChunkPilot-cf-runtime-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void RunGit(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }

    private static string RunGitOutput(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
        return output.Trim();
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private sealed class MetadataTransport(
        bool failCatalogResolve = false,
        string? armFailure = null) : ICertificationAgentTransport
    {
        public List<string> Operations { get; } = [];
        public object? ArmRequest { get; private set; }

        public Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<TResponse> SendAsync<TResponse>(
            string operation,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            object result = operation switch
            {
                "RegisterUiSession" => Registration((UiSessionRegistrationRequest)payload!),
                "HasCurseForgeApiKey" => new TextResponse("configured"),
                "CatalogProviderStatuses" => new[]
                {
                    new CatalogProviderStatus(CatalogProvider.CurseForge, true, "available")
                },
                "ResolveCatalogProject" => failCatalogResolve
                    ? throw new InvalidOperationException("protected credential could not be used")
                    : Project(),
                "Dashboard" => new DashboardSnapshot { AgentConnected = true },
                "SafeApplicationExit" => OperationResult.Ok("accepted"),
                "ArmCertificationUpdateFailure" => Arm(payload),
                _ => throw new InvalidOperationException("Unexpected operation: " + operation)
            };
            return Task.FromResult((TResponse)result);
        }

        private OperationResult Arm(object? payload)
        {
            ArmRequest = payload;
            if (armFailure is not null)
                throw new InvalidOperationException(armFailure);
            return OperationResult.Ok("armed");
        }

        private static UiSessionRegistrationResult Registration(UiSessionRegistrationRequest request) =>
            new(new ApplicationSession
            {
                SessionId = Guid.NewGuid(),
                ProcessId = request.ProcessId,
                ProcessCreationTicks = request.ProcessCreationTicks
            }, false, "")
            {
                SessionCapability = "test-capability"
            };
    }

    private sealed class FixedEndpointSource(
        IReadOnlyList<WindowsOwnedNetworkEndpoint> endpoints,
        Exception? failure = null) : IWindowsOwnedNetworkEndpointSource
    {
        public IReadOnlyList<WindowsOwnedNetworkEndpoint> Capture()
        {
            if (failure is not null)
                throw failure;
            return endpoints;
        }
    }

    private sealed class DelegateEndpointSource(
        Func<IReadOnlyList<WindowsOwnedNetworkEndpoint>> capture) :
        IWindowsOwnedNetworkEndpointSource
    {
        public IReadOnlyList<WindowsOwnedNetworkEndpoint> Capture() => capture();
    }

    private sealed class RecordingRecycleBin(
        string destination,
        string? expectedPendingEvidencePath = null,
        bool recycleConfirmed = true) : ICertificationRecycleBin
    {
        public bool Called { get; private set; }
        public bool PendingObservedBeforeMove { get; private set; }

        public bool MoveDirectory(string path)
        {
            Called = true;
            PendingObservedBeforeMove = expectedPendingEvidencePath is null ||
                                        File.Exists(expectedPendingEvidencePath);
            Directory.Move(path, destination);
            return recycleConfirmed;
        }
    }

    private sealed class NoOpRecycleBin : ICertificationRecycleBin
    {
        public bool Called { get; private set; }

        public bool MoveDirectory(string path)
        {
            Called = true;
            return false;
        }
    }
}
