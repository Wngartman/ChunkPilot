import { useCallback, useEffect, useRef, useState } from 'react';
import { Archive, Box, Check, ChevronLeft, ChevronRight, FolderOpen, Gamepad2, Globe2, HardDrive, Info, Plus, SlidersHorizontal, Wifi } from '../design-system/Icons';
import { Button, SearchInput, TextInput } from '../design-system/Primitives';
import { useAppStore } from '../state/store';
import { runMeasuredNavigation } from '../app/performance';
import { MemoryControl } from './memory/MemoryControl';
import { formatMemory } from './memory/memory';
import { modpackRouteLabel } from './modpacks/installationRoute';
import { VersionBrowser } from './versions/VersionBrowser';
import type { MinecraftVersionCatalog } from './versions/types';
import { ModpackPicker, type ModpackSelection, type ModpackSelectionChangeProvider,
  type ModpackSelectionChangeReason } from './modpacks/ModpackPicker';
import type { CreationLoaderBuildParameters, CurseForgeCreationReviewParameters, CurseForgeModpackPreflightParameters, ModpackSelectionMethod } from '../bridge/types';
import { isFixtureMode } from '../fixtures/mode';
import styles from './CreateServer.module.css';

interface Destination { available: boolean; path: string; message: string; }
interface CreationProgress {
  operationId?: string; revision?: number; startedAtUtc?: string; updatedAtUtc?: string;
  stage?: string; phase?: string; percent?: number; bytesDownloaded?: number; totalBytes?: number | null;
  bytesPerSecond?: number; currentArtifact?: string; message?: string; outcome?: string; isTerminal?: boolean;
  success?: boolean; error?: string; isIndeterminate?: boolean; stageElapsedSeconds?: number;
  lastMeaningfulStatus?: string; secondsSinceMeaningfulUpdate?: number; recentStatus?: string;
  newLogOutputObserved?: boolean; canRetry?: boolean; canDiscard?: boolean; retryGeneration?: number;
  retainedInputBytes?: number; statusUnavailable?: boolean;
}
type LoaderPlatform = 'Fabric' | 'Quilt' | 'Forge' | 'NeoForge' | 'LegacyFabric' | 'Ornithe';
type CreationPlatform = 'Vanilla' | 'Paper' | 'Modpack' | LoaderPlatform;
type PrimaryChoice = 'Vanilla' | 'Plugins' | 'Modpacks';
type PluginChoice = 'Recommended' | 'Exact';
type ModpackChoice = 'Browse' | 'Link' | 'Import' | 'Custom';
interface PaperBuild { id: number; label: string; channel: 'Stable' | 'Beta' | 'Alpha' | 'Unknown'; publishedAt: string | null; sizeBytes: number | null; selectable: boolean; support: 'Recommended' | 'Verified' | 'Experimental' | 'Unavailable'; supportReason: string; provenance: string; }
interface PaperBuildCatalog { available: boolean; message: string; fromCache: boolean; stale: boolean; retrievedAt: string | null; minecraftVersion: string; builds: PaperBuild[]; }
interface LoaderBuild { id: string; label: string; loaderVersion: string; installerVersion: string; channel: 'Stable' | 'Beta' | 'Experimental'; sizeBytes: number | null; hasIntegrityMetadata: boolean; canResolveIntegrity?: boolean; selectable: boolean; support: 'Recommended' | 'Verified' | 'Experimental' | 'Unavailable'; supportReason: string; provenance: string; }
interface LoaderBuildCatalog { platform: LoaderPlatform; available: boolean; message: string; fromCache: boolean; stale: boolean; retrievedAt: string | null; minecraftVersion: string; builds: LoaderBuild[]; }
interface LegacyArtifactSelection { cancelled: boolean; token: string; fileName: string; minecraftVersion: string; sizeBytes: number; sha256: string; matchesOfficialHash: boolean; identityEvidence: string; expiresAt: string; }
type WorldMode = 'New' | 'Upload';
interface ExistingWorldSelection { cancelled: boolean; token: string; displayName: string; kind: 'Folder' | 'ZipArchive'; worldName: string; sourceSizeBytes: number; expandedSizeBytes: number; fileCount: number; includesNether: boolean; includesEnd: boolean; expiresAt: string; }
type RemoteModpackSelection = Extract<ModpackSelection, { kind: 'remote' }>;
interface CurseForgeReviewState {
  reviewId: string;
  key: string;
  expiresAtMs: number | null;
  selection: RemoteModpackSelection;
}

const isLoaderPlatform = (value: CreationPlatform): value is LoaderPlatform =>
  value !== 'Vanilla' && value !== 'Paper' && value !== 'Modpack';
const loaderTitle = (platform: LoaderPlatform) => platform === 'LegacyFabric' ? 'Legacy Fabric' : platform;
const supportsHistoricalFileImport = (version: import('./versions/types').MinecraftVersionOption) =>
  !version.hasServerArtifact && ['1.0', 'b1.8', 'b1.8.1'].includes(version.id) &&
  version.javaMajor === 8 && version.launchProfile.kind !== 'Unknown';
const StatusPill = ({ text }: { text: string }) => <span className={styles.legacyArtifactBadge}>{text}</span>;
const curseForgeReviewLifetimeMs = 14 * 60_000;
const curseForgeReviewKey = (selectionMethod: ModpackSelectionMethod, selection: RemoteModpackSelection) =>
  `${selectionMethod}:${selection.project.projectId}:${selection.release.versionId}`;
const requireCurseForgePreflight = (selection: RemoteModpackSelection): RemoteModpackSelection => {
  const release = {
    ...selection.release,
    canCreate: false,
    preflightState: 'Required' as const,
    preflightDetail: 'This exact selection must be inspected again.',
    limitation: 'Inspecting the exact client manifest is required before this release can be created.'
  };
  return {
    ...selection,
    project: {
      ...selection.project,
      versions: selection.project.versions.map(candidate =>
        candidate.versionId === release.versionId ? release : candidate)
    },
    release
  };
};

const stages = [
  ['Game', 'Choose what to host'], ['Version', 'Choose Minecraft'], ['Performance', 'Set memory'], ['Server details', 'Name and identity'], ['Storage', 'Choose location'], ['Connectivity', 'Choose next step'], ['Review', 'Confirm and create']
] as const;

export function CreateServerPage({ onDone, onOpenProviderSettings, onActivity }: { onDone: () => void; onOpenProviderSettings?: () => void; onActivity?: () => void }) {
  const command = useAppStore(state => state.command);
  const bridge = useAppStore(state => state.bridge);
  const [recoveryRevision, setRecoveryRevision] = useState(0);
  const [recoveryBusy, setRecoveryBusy] = useState(false);
  const hostTotalBytes = useAppStore(state => state.snapshot?.host.totalMemoryBytes);
  const fixtureMode = isFixtureMode();
  const requestedMode = new URLSearchParams(window.location.search).get('mode')?.toLowerCase();
  const requestedPlatform: CreationPlatform = requestedMode === 'paper' ? 'Paper'
    : requestedMode === 'modpack' ? 'Modpack'
    : requestedMode === 'fabric' ? 'Fabric'
      : requestedMode === 'quilt' ? 'Quilt'
        : requestedMode === 'forge' ? 'Forge'
          : requestedMode === 'neoforge' ? 'NeoForge'
            : requestedMode === 'legacyfabric' ? 'LegacyFabric'
              : requestedMode === 'ornithe' ? 'Ornithe'
                : 'Vanilla';
  const requestedStage = Number(new URLSearchParams(window.location.search).get('stage') ?? 0);
  const [step, setStep] = useState(Number.isInteger(requestedStage) && requestedStage >= 0 && requestedStage < stages.length ? requestedStage : 0); const [catalog, setCatalog] = useState<MinecraftVersionCatalog | null>(null); const [catalogError, setCatalogError] = useState('');
  const goToStep = (next: number) => {
    if (step === 2 && next > step && !memoryValid) return;
    runMeasuredNavigation(`create-step-${stages[next][0].toLowerCase().replace(' ', '-')}`, () => setStep(next));
  };
  const [platform, setPlatform] = useState<CreationPlatform>(requestedPlatform);
  const [primaryChoice, setPrimaryChoice] = useState<PrimaryChoice>(requestedPlatform === 'Vanilla' ? 'Vanilla'
    : requestedPlatform === 'Paper' ? 'Plugins' : 'Modpacks');
  const [pluginChoice, setPluginChoice] = useState<PluginChoice>('Recommended');
  const [modpackChoice, setModpackChoice] = useState<ModpackChoice>(isLoaderPlatform(requestedPlatform) ? 'Custom' : 'Browse');
  const disclosureRef = useRef<HTMLDivElement>(null);
  const focusDisclosure = useRef(false);
  const [versionId, setVersionId] = useState(fixtureMode ? '1.21.8' : ''); const [ramMb, setRamMb] = useState(4096); const [initialRamMb, setInitialRamMb] = useState(1024); const [name, setName] = useState(fixtureMode ? 'Copper Harbor' : ''); const [instanceRoot, setInstanceRoot] = useState(''); const [destination, setDestination] = useState<Destination | null>(fixtureMode ? { available: true, path: 'C:\\ChunkPilot Servers\\Copper-Harbor', message: 'This destination is available.' } : null); const [networking, setNetworking] = useState('HomeNetwork'); const [port, setPort] = useState(25565); const [eula, setEula] = useState(fixtureMode && requestedStage === 6); const [submitted, setSubmitted] = useState(false); const [operationId, setOperationId] = useState(''); const [progress, setProgress] = useState<CreationProgress | null>(null);
  const [paperBuildCatalog, setPaperBuildCatalog] = useState<PaperBuildCatalog | null>(null);
  const [maximumMemoryValid, setMaximumMemoryValid] = useState(true);
  const [initialMemoryValid, setInitialMemoryValid] = useState(true);
  const memoryValid = maximumMemoryValid && initialMemoryValid && initialRamMb >= 512 && initialRamMb <= ramMb;
  const [paperBuildId, setPaperBuildId] = useState<number | null>(null);
  const [paperBuildError, setPaperBuildError] = useState('');
  const [loaderBuildCatalog, setLoaderBuildCatalog] = useState<LoaderBuildCatalog | null>(null);
  const [loaderVersion, setLoaderVersion] = useState('');
  const [loaderBuildError, setLoaderBuildError] = useState('');
  const [resolvingLoaderId, setResolvingLoaderId] = useState('');
  const focusedLoaderRequest = useRef<AbortController | null>(null);
  const loaderCatalogGeneration = useRef(0);
  const [experimentalAccepted, setExperimentalAccepted] = useState(false);
  const [modpack, setModpack] = useState<ModpackSelection | null>(null);
  const [modpackReviewId, setModpackReviewId] = useState('');
  const modpackReviewIdRef = useRef('');
  const curseForgeReviewRef = useRef<CurseForgeReviewState | null>(null);
  const nativeReviewInvalidationRef = useRef<Promise<void>>(Promise.resolve());
  const [legacyArtifact, setLegacyArtifact] = useState<LegacyArtifactSelection | null>(null);
  const [legacyArtifactError, setLegacyArtifactError] = useState('');
  const [worldMode, setWorldMode] = useState<WorldMode>('New');
  const [existingWorld, setExistingWorld] = useState<ExistingWorldSelection | null>(null);
  const [worldError, setWorldError] = useState('');
  const [catalogRequest, setCatalogRequest] = useState(0);
  const submittedOperationId = useRef('');
  const remoteModpackSelectionMethod: ModpackSelectionMethod = modpackChoice === 'Link' ? 'Link' : 'Browse';
  const invalidateModpackReview = useCallback(() => {
    curseForgeReviewRef.current = null;
    modpackReviewIdRef.current = '';
    setModpackReviewId('');
    const activeBridge = bridge;
    nativeReviewInvalidationRef.current = nativeReviewInvalidationRef.current
      .catch(() => undefined)
      .then(async () => {
        if (activeBridge) await activeBridge.request('modpacks.invalidatePreflight');
      })
      .catch(() => undefined);
  }, [bridge]);
  const startCurseForgeReview = useCallback((selection: RemoteModpackSelection) => {
    const reviewedSelection = requireCurseForgePreflight(selection);
    const reviewId = crypto.randomUUID();
    curseForgeReviewRef.current = {
      reviewId,
      key: curseForgeReviewKey(remoteModpackSelectionMethod, reviewedSelection),
      expiresAtMs: null,
      selection: reviewedSelection
    };
    modpackReviewIdRef.current = reviewId;
    setModpackReviewId(reviewId);
    setModpack(reviewedSelection);
  }, [remoteModpackSelectionMethod]);
  const remotePreflightKey = modpack?.kind === 'remote' && modpack.project.provider === 'CurseForge'
    ? `${modpackReviewId}:${remoteModpackSelectionMethod}:${modpack.project.projectId}:${modpack.release.versionId}` : '';
  const remotePreflightState = modpack?.kind === 'remote' && modpack.project.provider === 'CurseForge'
    ? modpack.release.preflightState : undefined;
  const worldUploadUnavailable = platform === 'Modpack' && modpack?.kind === 'local' &&
    modpack.local.inspection?.sourceKind !== 'ModrinthPack';
  useEffect(() => {
    if (!worldUploadUnavailable) return;
    setWorldMode('New');
    setExistingWorld(null);
    setWorldError('');
  }, [worldUploadUnavailable]);
  useEffect(() => {
    if (!bridge || !remotePreflightKey || !modpackReviewId) return;
    const selected = modpack?.kind === 'remote' ? modpack : null;
    if (!selected || selected.project.provider !== 'CurseForge' ||
      selected.release.preflightState !== 'Required') return;
    const projectId = selected.project.projectId;
    const versionId = selected.release.versionId;
    const reviewId = modpackReviewId;
    const modpackSelectionMethod = remoteModpackSelectionMethod;
    const controller = new AbortController();
    const preflightParameters: CurseForgeModpackPreflightParameters = {
      projectId, versionId, reviewId, modpackSelectionMethod
    };
    void (async () => {
      try {
        await nativeReviewInvalidationRef.current;
        if (controller.signal.aborted || modpackReviewIdRef.current !== reviewId) return;
        const release = await bridge.request<import('../bridge/types').ModpackRelease>(
          'modpacks.preflight', preflightParameters, controller.signal);
        if (controller.signal.aborted || modpackReviewIdRef.current !== reviewId) return;
        if (!release || typeof release !== 'object' || release.versionId !== versionId ||
          typeof release.versionName !== 'string' || !release.versionName.trim() ||
          typeof release.minecraftVersion !== 'string' || !release.minecraftVersion.trim() ||
          typeof release.loader !== 'string' || !release.loader.trim() ||
          typeof release.canCreate !== 'boolean' ||
          !['Stable', 'Beta', 'Alpha'].includes(release.releaseChannel) ||
          !['NotRequired', 'Required', 'Inspecting', 'Ready', 'Unsupported', 'Failed'].includes(release.preflightState ?? ''))
          throw new Error('The native CurseForge review returned an invalid or mismatched release. Select the release again to retry.');
        const reviewedSelection: RemoteModpackSelection = {
          ...selected,
          project: {
            ...selected.project,
            versions: selected.project.versions.map(candidate =>
              candidate.versionId === versionId ? release : candidate)
          },
          release
        };
        if (release.preflightState === 'Ready' && curseForgeReviewRef.current?.reviewId === reviewId) {
          curseForgeReviewRef.current = {
            ...curseForgeReviewRef.current,
            expiresAtMs: Date.now() + curseForgeReviewLifetimeMs,
            selection: reviewedSelection
          };
        } else if (curseForgeReviewRef.current?.reviewId === reviewId) {
          curseForgeReviewRef.current = null;
        }
        setModpack(current => current?.kind === 'remote' &&
          current.project.provider === 'CurseForge' && current.project.projectId === projectId &&
          current.release.versionId === versionId && modpackReviewIdRef.current === reviewId
          ? reviewedSelection
          : current);
      } catch (reason) {
        if (controller.signal.aborted || modpackReviewIdRef.current !== reviewId) return;
        if (curseForgeReviewRef.current?.reviewId === reviewId)
          curseForgeReviewRef.current = null;
        const limitation = reason instanceof Error ? reason.message
          : 'The exact CurseForge client manifest could not be inspected.';
        setModpack(current => current?.kind === 'remote' &&
          current.project.provider === 'CurseForge' && current.project.projectId === projectId &&
          current.release.versionId === versionId && modpackReviewIdRef.current === reviewId
          ? { ...current, release: { ...current.release, canCreate: false, preflightState: 'Failed', limitation } }
          : current);
      }
    })();
    return () => controller.abort();
    // The exact native invalidation is serialized ahead of the next preflight so an older clear
    // request cannot arrive late and erase newly returned evidence.
  }, [bridge, modpackReviewId, remoteModpackSelectionMethod, remotePreflightKey, remotePreflightState]);
  useEffect(() => {
    const review = curseForgeReviewRef.current;
    if (submitted || operationId || remotePreflightState !== 'Ready' || !review?.expiresAtMs ||
      review.reviewId !== modpackReviewId) return;
    const timer = window.setTimeout(() => {
      if (submittedOperationId.current) return;
      const current = curseForgeReviewRef.current;
      if (!current || current.reviewId !== review.reviewId || !current.expiresAtMs ||
        current.expiresAtMs > Date.now()) return;
      const selection = current.selection;
      invalidateModpackReview();
      startCurseForgeReview(selection);
    }, Math.max(0, review.expiresAtMs - Date.now()) + 25);
    return () => window.clearTimeout(timer);
  }, [invalidateModpackReview, modpackReviewId, operationId, remotePreflightState,
    startCurseForgeReview, submitted]);
  useEffect(() => {
    if (!bridge || operationId) return;
    let active = true;
    const remembered = window.sessionStorage.getItem('chunkpilot.creation.operation');
    void bridge.request<CreationProgress[]>('creation.operations').then(operations => {
      if (!active) return;
      const recoverable = (operation: CreationProgress) => !operation.isTerminal || operation.canDiscard;
      const current = operations.find(operation => operation.operationId === remembered && recoverable(operation))
        ?? operations.find(recoverable);
      if (!current?.operationId) {
        if (remembered) window.sessionStorage.removeItem('chunkpilot.creation.operation');
        return;
      }
      submittedOperationId.current = current.operationId;
      setOperationId(current.operationId);
      setProgress(current);
      setSubmitted(true);
      setStep(6);
    }).catch(() => { /* New creation remains available when no prior operation can be recovered. */ });
    return () => { active = false; };
  }, [bridge, operationId]);
  useEffect(() => {
    if (platform === 'Modpack') { setCatalog(null); setCatalogError(''); return; }
    let active = true;
    setCatalogError('');
    const applyCatalog = (result: MinecraftVersionCatalog) => {
      if (!active) return;
      setCatalog(result);
      setVersionId(current => {
        if (current && result.versions.some(version => version.id === current)) return current;
        return result.versions.find(version => version.id === result.latestVerifiedReleaseId && version.selectable)?.id
          ?? result.versions.find(version => version.id === result.manifestLatestReleaseId && version.selectable)?.id
          ?? result.versions.find(version => version.support === 'Recommended' && version.selectable)?.id
          ?? result.versions.find(version => version.releaseKind === 'Release' && version.selectable)?.id
          ?? result.versions.find(version => version.selectable)?.id
          ?? '';
      });
      setCatalogError(result.available ? '' : result.message || 'Version catalog unavailable.');
    };
    void command<MinecraftVersionCatalog>('creation.catalog', { platform, includeSnapshots: true, forceRefresh: catalogRequest > 0 })
      .then(result => {
        applyCatalog(result);
        if (result.stale && active) {
          void command<MinecraftVersionCatalog>('creation.catalog', { platform, includeSnapshots: true, forceRefresh: true })
            .then(applyCatalog)
            .catch(() => { /* the last-known-good catalog remains usable */ });
        }
      })
      .catch(error => { if (active) setCatalogError(error instanceof Error ? error.message : 'Version catalog unavailable.'); });
    return () => { active = false; };
  }, [command, catalogRequest, platform]);
  useEffect(() => {
    if (platform !== 'Paper' || !versionId) { setPaperBuildCatalog(null); setPaperBuildId(null); setPaperBuildError(''); return; }
    let active = true;
    setPaperBuildError('');
    const applyPaperBuilds = (result: PaperBuildCatalog) => {
      if (!active) return;
      setPaperBuildCatalog(result);
      setPaperBuildError(result.available ? '' : result.message || 'Paper build catalog unavailable.');
      setPaperBuildId(current => result.builds.some(build => build.id === current && build.selectable)
        ? current
        : result.builds.find(build => build.selectable && build.support === 'Recommended')?.id
          ?? result.builds.find(build => build.selectable && build.support === 'Verified')?.id
          ?? result.builds.find(build => build.selectable && build.channel === 'Stable')?.id
          ?? result.builds.find(build => build.selectable)?.id
          ?? null);
    };
    void command<PaperBuildCatalog>('creation.paperBuilds', { versionId })
      .then(result => {
        applyPaperBuilds(result);
        if (result.stale && active)
          void command<PaperBuildCatalog>('creation.paperBuilds', { versionId, forceRefresh: true })
            .then(applyPaperBuilds)
            .catch(() => { /* the last-known-good Paper build inventory remains usable */ });
      })
      .catch(error => { if (active) setPaperBuildError(error instanceof Error ? error.message : 'Paper build catalog unavailable.'); });
    return () => { active = false; };
  }, [command, platform, versionId]);
  useEffect(() => {
    loaderCatalogGeneration.current += 1;
    focusedLoaderRequest.current?.abort(); focusedLoaderRequest.current = null;
    setResolvingLoaderId(''); setLoaderBuildCatalog(null); setLoaderVersion('');
    if (!isLoaderPlatform(platform) || !versionId) {
      setLoaderBuildCatalog(null); setLoaderVersion(''); setLoaderBuildError(''); return;
    }
    let active = true;
    setLoaderBuildError('');
    void command<LoaderBuildCatalog>('creation.loaderBuilds', { platform, versionId } satisfies CreationLoaderBuildParameters)
      .then(result => {
        if (!active) return;
        setLoaderBuildCatalog(result);
        setLoaderBuildError(result.available ? '' : result.message || `${platform} loader catalog unavailable.`);
        setLoaderVersion(current => result.builds.some(build => build.id === current && build.selectable)
          ? current
          : result.builds.find(build => build.selectable && build.support === 'Recommended')?.id
            ?? result.builds.find(build => build.selectable && build.support === 'Verified')?.id
            ?? result.builds.find(build => build.selectable && build.channel === 'Stable')?.id
            ?? result.builds.find(build => build.selectable)?.id
            ?? '');
      })
      .catch(error => { if (active) setLoaderBuildError(error instanceof Error ? error.message : `${platform} loader catalog unavailable.`); });
    return () => { active = false; focusedLoaderRequest.current?.abort(); focusedLoaderRequest.current = null; };
  }, [command, platform, versionId]);
  const chooseLoaderBuild = (id: string) => {
    const build = loaderBuildCatalog?.builds.find(item => item.id === id);
    if (!build || (!build.selectable && !build.canResolveIntegrity) || !bridge) return;
    focusedLoaderRequest.current?.abort();
    focusedLoaderRequest.current = null;
    setLoaderVersion(id); setExperimentalAccepted(false); setLoaderBuildError(''); setResolvingLoaderId('');
    if (build.selectable) return;
    const controller = new AbortController(); focusedLoaderRequest.current = controller;
    const generation = loaderCatalogGeneration.current;
    setResolvingLoaderId(id);
    void bridge.request<LoaderBuildCatalog>('creation.loaderBuilds', {
      platform, versionId, loaderVersion: build.loaderVersion
    } satisfies CreationLoaderBuildParameters, controller.signal).then(result => {
      if (controller.signal.aborted || generation !== loaderCatalogGeneration.current) return;
      const exact = result.platform === platform && result.minecraftVersion === versionId && Array.isArray(result.builds)
        ? result.builds.find(item => item.id === id && item.loaderVersion === build.loaderVersion) : null;
      if (!exact) throw new Error('The provider did not return this exact loader version. Select a build again.');
      if (!result.available || !exact.selectable || !exact.hasIntegrityMetadata)
        throw new Error(exact.supportReason || result.message || 'The official checksum is unavailable for this build.');
      setLoaderBuildCatalog(result); setLoaderVersion(id);
    }).catch(error => {
      if (!controller.signal.aborted && generation === loaderCatalogGeneration.current)
        setLoaderBuildError(error instanceof Error ? error.message : 'This exact loader could not be verified.');
    }).finally(() => {
      if (focusedLoaderRequest.current === controller) { focusedLoaderRequest.current = null; setResolvingLoaderId(''); }
    });
  };
  useEffect(() => { if (!name.trim()) { setDestination(null); return; } const timer = window.setTimeout(() => { void command<Destination>('creation.previewDestination', { name: name.trim(), instanceRoot }).then(setDestination).catch(() => setDestination(null)); }, 300); return () => window.clearTimeout(timer); }, [name, instanceRoot]);
  useEffect(() => {
    if (!operationId) return;
    let active = true;
    let timer = 0;
    let failures = 0;
    const poll = async () => {
      try {
        if (!bridge) return;
        const next = await bridge.request<CreationProgress>('creation.progress', { operationId });
        if (!active) return;
        failures = 0;
        setProgress(next);
        if (next.isTerminal) {
          if (!next.canDiscard) window.sessionStorage.removeItem('chunkpilot.creation.operation');
          submittedOperationId.current = '';
        } else timer = window.setTimeout(poll, 750);
      } catch {
        if (!active) return;
        failures += 1;
        if (failures >= 6) {
          setProgress(current => ({ ...current, statusUnavailable: true, message: 'Progress is unavailable. The Agent may still be working; retry the status check or open Activity.' }));
          return;
        }
        setProgress(current => ({ ...current, operationId, message: `Reconnecting to the accepted operation${failures > 1 ? ` (attempt ${failures})` : ''}…`, isTerminal: false }));
        timer = window.setTimeout(poll, Math.min(5_000, 500 * 2 ** Math.min(failures, 3)));
      }
    };
    void poll();
    return () => { active = false; window.clearTimeout(timer); };
  }, [bridge, operationId, recoveryRevision]);
  const selectedVersion = catalog?.versions.find(version => version.id === versionId);
  const selectedPaperBuild = paperBuildCatalog?.builds.find(build => build.id === paperBuildId) ?? null;
  const selectedLoaderBuild = loaderBuildCatalog?.builds.find(build => build.id === loaderVersion) ?? null;
  const requiresExperimentalAck = legacyArtifact !== null || platform === 'Modpack' ? true : platform === 'Paper'
    ? selectedPaperBuild?.support === 'Experimental'
    : isLoaderPlatform(platform)
      ? selectedLoaderBuild?.support === 'Experimental'
    : selectedVersion?.support === 'Experimental';
  const hasExactSelection = platform === 'Modpack' ? modpack !== null && (modpack.kind === 'local'
    ? Boolean(modpack.local.inspection?.canCreate) && ((modpack.local.inspection?.launchCandidates.length ?? 0) <= 1 || Boolean(modpack.local.launchRelativePath))
    : modpack.release.canCreate && (modpack.project.provider !== 'CurseForge' || Boolean(modpackReviewId))) : (selectedVersion?.selectable === true || (platform === 'Vanilla' && legacyArtifact?.minecraftVersion === selectedVersion?.id)) && (platform === 'Vanilla' ||
    (platform === 'Paper' ? selectedPaperBuild?.selectable === true : selectedLoaderBuild?.selectable === true));
  const canNext = (step === 0 && (primaryChoice !== 'Modpacks' || Boolean(modpackChoice))) || (step === 1 && hasExactSelection) || (step === 2 && memoryValid) || (step === 3 && name.trim().length > 0) || (step === 4 && destination?.available === true && (worldMode === 'New' || existingWorld !== null)) || step === 5 || (step === 6 && memoryValid && eula && (!requiresExperimentalAck || experimentalAccepted));
  const chooseFolder = () => void command<{ path: string }>('creation.chooseFolder', { startingPath: instanceRoot }).then(result => setInstanceRoot(result.path));
  const create = () => {
    if (!bridge || !eula || submitted || submittedOperationId.current || (platform !== 'Modpack' && !selectedVersion) || !hasExactSelection || !memoryValid || (requiresExperimentalAck && !experimentalAccepted)) return;
    const selectedCurseForge = modpack?.kind === 'remote' && modpack.project.provider === 'CurseForge'
      ? modpack : null;
    if (selectedCurseForge) {
      const review = curseForgeReviewRef.current;
      if (!review || review.reviewId !== modpackReviewId ||
          review.key !== curseForgeReviewKey(remoteModpackSelectionMethod, selectedCurseForge) ||
          !review.expiresAtMs || review.expiresAtMs <= Date.now()) {
        invalidateModpackReview();
        startCurseForgeReview(selectedCurseForge);
        return;
      }
    }
    const requestedOperationId = crypto.randomUUID();
    submittedOperationId.current = requestedOperationId;
    window.sessionStorage.setItem('chunkpilot.creation.operation', requestedOperationId);
    setSubmitted(true);
    setOperationId(requestedOperationId);
    const curseForgeReview: CurseForgeCreationReviewParameters | Record<string, never> =
      modpack?.kind === 'remote' && modpack.project.provider === 'CurseForge'
        ? { modpackReviewId, modpackSelectionMethod: remoteModpackSelectionMethod }
        : {};
    void bridge.request<{ operationId: string }>('creation.begin', { operationId: requestedOperationId, platform, name: name.trim(), versionId, buildId: selectedPaperBuild?.id, loaderVersion: selectedLoaderBuild?.loaderVersion, modpackProvider: modpack?.kind === 'remote' ? modpack.project.provider : undefined, modpackProjectId: modpack?.kind === 'remote' ? modpack.project.projectId : undefined, modpackVersionId: modpack?.kind === 'remote' ? modpack.release.versionId : undefined, ...curseForgeReview, localPackToken: modpack?.kind === 'local' ? modpack.local.token : undefined, importManagementMode: modpack?.kind === 'local' ? modpack.local.managementMode : undefined, importLaunchCandidate: modpack?.kind === 'local' ? modpack.local.launchRelativePath : undefined, legacyArtifactToken: legacyArtifact?.token, initialWorldToken: worldMode === 'Upload' ? existingWorld?.token : undefined, minimumRamMb: initialRamMb, maximumRamMb: ramMb, port, networking, instanceRoot, experimentalAccepted, eulaAccepted: true })
      .then(result => { if (result.operationId !== requestedOperationId) throw new Error('ChunkPilot returned an unexpected creation operation identity.'); })
      .catch(() => { /* Progress polling owns the authoritative terminal result even if the acceptance response was lost. */ });
  };
  const cancelCreation = () => void command('creation.cancel', { operationId });
  const recoverCreation = async (action: 'creation.retry' | 'creation.discard') => {
    if (recoveryBusy || !operationId) return;
    setRecoveryBusy(true);
    try {
      await command(action, { operationId, retryGeneration: progress?.retryGeneration ?? 0 });
      if (action === 'creation.retry') {
        window.sessionStorage.setItem('chunkpilot.creation.operation', operationId);
        setProgress(current => ({ ...current, isTerminal: false, canRetry: false, canDiscard: false,
          error: undefined, isIndeterminate: true, message: 'Rechecking the exact release and retained archive' }));
        setRecoveryRevision(value => value + 1);
      } else {
        window.sessionStorage.removeItem('chunkpilot.creation.operation');
        setProgress(current => ({ ...current, canRetry: false, canDiscard: false, retainedInputBytes: 0,
          error: undefined, message: 'Retained download discarded. Your chosen server folder was not changed.' }));
      }
    } catch (error) {
      setProgress(current => ({ ...current, error: error instanceof Error ? error.message : 'Recovery could not be completed.' }));
    } finally { setRecoveryBusy(false); }
  };
  useEffect(() => {
    if (!focusDisclosure.current) return;
    focusDisclosure.current = false;
    window.requestAnimationFrame(() => disclosureRef.current?.querySelector<HTMLElement>('button, select')?.focus());
  }, [primaryChoice]);
  const clearDownstream = () => { setCatalog(null); setVersionId(''); setPaperBuildId(null); setLoaderVersion(''); setModpack(null); invalidateModpackReview(); setLegacyArtifact(null); setLegacyArtifactError(''); setExperimentalAccepted(false); };
  const choosePlatform = (next: CreationPlatform) => { setPlatform(next); clearDownstream(); };
  const choosePrimary = (next: PrimaryChoice) => {
    if (next === primaryChoice) return;
    focusDisclosure.current = true;
    setPrimaryChoice(next);
    if (next === 'Vanilla') choosePlatform('Vanilla');
    else if (next === 'Plugins') choosePlatform('Paper');
    else choosePlatform(modpackChoice === 'Custom' && isLoaderPlatform(platform) ? platform : 'Modpack');
  };
  const chooseModpackPath = (next: ModpackChoice) => {
    if (next === modpackChoice) return;
    setModpackChoice(next);
    choosePlatform(next === 'Custom' ? 'Fabric' : 'Modpack');
  };
  const chooseLegacyArtifact = () => {
    if (!selectedVersion) return;
    setLegacyArtifactError('');
    void command<LegacyArtifactSelection>('creation.chooseLegacyArtifact', { versionId: selectedVersion.id })
      .then(result => { if (!result.cancelled) { setLegacyArtifact(result); setExperimentalAccepted(false); } })
      .catch(error => setLegacyArtifactError(error instanceof Error ? error.message : 'The server JAR could not be reviewed.'));
  };
  const chooseWorld = (kind: 'folder' | 'zip') => {
    setWorldError('');
    void command<ExistingWorldSelection>('creation.chooseWorld', { kind })
      .then(result => { if (!result.cancelled) { setExistingWorld(result); setWorldMode('Upload'); } })
      .catch(error => setWorldError(error instanceof Error ? error.message : 'The selected world could not be reviewed.'));
  };
  const selectModpack = (selection: ModpackSelection | null, reason: ModpackSelectionChangeReason,
    sourceProvider: ModpackSelectionChangeProvider) => {
    const curseForge = selection?.kind === 'remote' && selection.project.provider === 'CurseForge'
      ? selection : null;
    if (curseForge) {
      const key = curseForgeReviewKey(remoteModpackSelectionMethod, curseForge);
      const cached = curseForgeReviewRef.current;
      if (cached?.key === key && cached.expiresAtMs && cached.expiresAtMs > Date.now()) {
        modpackReviewIdRef.current = cached.reviewId;
        setModpackReviewId(cached.reviewId);
        setModpack(cached.selection);
      } else {
        if (cached) invalidateModpackReview();
        else {
          modpackReviewIdRef.current = '';
          setModpackReviewId('');
        }
        startCurseForgeReview(curseForge);
      }
    } else {
      modpackReviewIdRef.current = '';
      setModpackReviewId('');
      if (reason === 'local' || sourceProvider === 'CurseForge' && reason !== 'provider-navigation')
        invalidateModpackReview();
      setModpack(selection);
    }
    if (!name.trim() && selection?.kind === 'local' && selection.local.inspection?.name)
      setName(selection.local.inspection.name);
    setExperimentalAccepted(false);
  };
  const body = step === 0 ? <div className={styles.gameSelection}><div className={styles.primaryChoices}>
    <button className={`${styles.choice} ${styles.primaryChoice}`} aria-pressed={primaryChoice === 'Vanilla'} data-selected={primaryChoice === 'Vanilla'} onClick={() => choosePrimary('Vanilla')}><span className={styles.choiceIcon}><Gamepad2 size={19} /></span><span><strong>Vanilla</strong><p>Unmodified Minecraft using the official dedicated server.</p></span><span className={styles.radio} /></button>
    <button className={`${styles.choice} ${styles.primaryChoice}`} aria-pressed={primaryChoice === 'Plugins'} data-selected={primaryChoice === 'Plugins'} onClick={() => choosePrimary('Plugins')}><span className={styles.choiceIcon}><Box size={19} /></span><span><strong>Plugins</strong><p>Performance-focused Minecraft with server-side plugins.</p></span><span className={styles.radio} /></button>
    <button className={`${styles.choice} ${styles.primaryChoice}`} aria-pressed={primaryChoice === 'Modpacks'} data-selected={primaryChoice === 'Modpacks'} onClick={() => choosePrimary('Modpacks')}><span className={styles.choiceIcon}><SlidersHorizontal size={19} /></span><span><strong>Modpacks</strong><p>Browse a pack, import one, or build a custom modded server.</p></span><span className={styles.radio} /></button>
  </div>
  {primaryChoice === 'Plugins' && <div ref={disclosureRef} className={styles.disclosure} aria-label="Plugin server choices">
    <span className={styles.disclosureIntro}><strong>Paper server</strong><small>Paper is ChunkPilot’s recommended plugin platform.</small></span>
    <button type="button" data-selected={pluginChoice === 'Recommended'} onClick={() => { setPluginChoice('Recommended'); choosePlatform('Paper'); }}><strong>Recommended Paper</strong><small>Use the latest certified stable build.</small></button>
    <button type="button" data-selected={pluginChoice === 'Exact'} onClick={() => { setPluginChoice('Exact'); choosePlatform('Paper'); }}><strong>Choose exact version</strong><small>Select Minecraft and Paper build next.</small></button>
  </div>}
  {primaryChoice === 'Modpacks' && <div ref={disclosureRef} className={styles.disclosure} aria-label="Modpack server choices">
    <button type="button" data-selected={modpackChoice === 'Browse'} onClick={() => chooseModpackPath('Browse')}><strong>Browse modpacks</strong><small>Search Modrinth or connected CurseForge.</small></button>
    <button type="button" data-selected={modpackChoice === 'Link'} onClick={() => chooseModpackPath('Link')}><strong>Paste provider link</strong><small>Open one exact pack from its official project link.</small></button>
    <button type="button" data-selected={modpackChoice === 'Import'} onClick={() => chooseModpackPath('Import')}><strong>Import pack file</strong><small>Review a local provider pack before creating.</small></button>
    <button type="button" data-selected={modpackChoice === 'Custom'} onClick={() => chooseModpackPath('Custom')}><strong>Build a custom modded server</strong><small>Choose a loader and add your own content.</small></button>
    {modpackChoice === 'Custom' && <label className={styles.loaderDisclosure}>Loader<select aria-label="Custom modded server loader" value={isLoaderPlatform(platform) ? platform : 'Fabric'} onChange={event => choosePlatform(event.target.value as LoaderPlatform)}><option value="Fabric">Fabric</option><option value="NeoForge">NeoForge</option><option value="Forge">Forge</option><option value="Quilt">Quilt</option><option value="LegacyFabric">Legacy Fabric — Experimental</option><option value="Ornithe">Ornithe — Experimental</option></select></label>}
  </div>}
  <div className={styles.readinessFacts}><div><Check size={14} /><span><strong>Managed Java</strong><small>A compatible runtime is selected automatically.</small></span></div><div><HardDrive size={14} /><span><strong>Persistent storage</strong><small>Your world lives outside the application folder.</small></span></div><div><Globe2 size={14} /><span><strong>Private by default</strong><small>Creation does not open your firewall or router.</small></span></div></div></div>
  : step === 1 ? platform === 'Modpack' ? <ModpackPicker value={modpack} initialMode={modpackChoice} onOpenProviderSettings={onOpenProviderSettings} onChange={selectModpack} /> : <>{catalogError ? <div className={styles.catalogError}><p className={styles.error}>{catalogError}</p><Button onClick={() => setCatalogRequest(value => value + 1)}>Retry official catalog</Button></div> : catalog ? <>{versionId && !selectedVersion && <p className={styles.error}>Your previous selection is no longer in the current catalog. Choose a version again.</p>}<VersionBrowser catalog={catalog} value={versionId} allowUnavailableSelection={platform === 'Vanilla' ? supportsHistoricalFileImport : undefined} onChange={version => { setVersionId(version.id); setPaperBuildId(null); setLoaderVersion(''); setLegacyArtifact(null); setLegacyArtifactError(''); setExperimentalAccepted(false); }} compact={platform !== 'Vanilla'} />{platform === 'Vanilla' && selectedVersion && supportsHistoricalFileImport(selectedVersion) && <section className={styles.legacyArtifact}><div><strong>Original server files required</strong><p>Mojang's current metadata no longer publishes this dedicated-server JAR. Choose your own legitimate copy; ChunkPilot inspects and hashes it without running it, copies it through managed staging, and leaves the source unchanged.</p></div>{legacyArtifact ? <div className={styles.legacyArtifactResult}><span><strong>{legacyArtifact.fileName}</strong><small>{(legacyArtifact.sizeBytes / 1024 / 1024).toFixed(1)} MB · SHA-256 {legacyArtifact.sha256.slice(0, 12)}…</small></span><StatusPill text="Reviewed locally" /><Button onClick={chooseLegacyArtifact}>Replace</Button></div> : <Button variant="primary" onClick={chooseLegacyArtifact}>Choose server JAR</Button>}{legacyArtifact && <p>{legacyArtifact.identityEvidence}</p>}{legacyArtifactError && <p className={styles.error}>{legacyArtifactError}</p>}</section>}{platform === 'Paper' && selectedVersion && <PaperBuildPicker catalog={paperBuildCatalog} value={paperBuildId} error={paperBuildError} onChange={setPaperBuildId} />}{isLoaderPlatform(platform) && selectedVersion && <LoaderBuildPicker platform={platform} catalog={loaderBuildCatalog} value={loaderVersion} error={loaderBuildError} resolvingId={resolvingLoaderId} onChange={chooseLoaderBuild} />}</> : <p className={styles.loading}>Loading the official {platform === 'Paper' ? 'PaperMC' : isLoaderPlatform(platform) ? loaderTitle(platform) : 'Minecraft'} version inventory…</p>}</>
  : step === 2 ? <><div className={styles.memoryIntro}><strong>Memory</strong><p>Most small {platform} servers run well with 2–4 GB. Choose a preset or enter an exact amount.</p></div><MemoryControl valueMib={ramMb} onChange={value => { setRamMb(value); if (initialRamMb > value) setInitialRamMb(value); }} onValidityChange={setMaximumMemoryValid} hostTotalBytes={hostTotalBytes} ariaLabel="Maximum server memory" /><details className={styles.advanced}><summary>Advanced memory details</summary><p>Initial memory is reserved when Java starts. It must not exceed the maximum.</p><MemoryControl valueMib={initialRamMb} onChange={setInitialRamMb} onValidityChange={setInitialMemoryValid} hostTotalBytes={hostTotalBytes} ariaLabel="Initial server memory" minimumMib={512} maximumMib={ramMb} /></details><div className={styles.notice}><Info size={16} /><span>{platform === 'Modpack' && modpack ? `This pack requires Java ${modpack.kind === 'remote' ? modpack.release.requiredJavaMajor : modpack.local.inspection?.requiredJavaMajor}. ChunkPilot will verify the pack manifest and exact loader before activation.` : selectedVersion?.javaMajor ? `${platform} ${selectedVersion.id} requires Java ${selectedVersion.javaMajor}. ChunkPilot will use a compatible managed runtime or install one during creation.` : 'Choose a version to establish the required Java runtime.'}</span></div></>
  : step === 3 ? <div className={styles.form}><div className={styles.field}><label htmlFor="serverName">Server name</label><TextInput id="serverName" value={name} onChange={event => setName(event.target.value)} placeholder="My Minecraft Server" autoFocus /><small>This changes ChunkPilot's display name. It does not rename a world.</small></div><div className={styles.field}><label htmlFor="serverPort">Server port</label><TextInput id="serverPort" type="number" min={1} max={65535} value={port} onChange={event => setPort(Number(event.target.value))} /><small>Port availability remains unknown until startup. Creation does not open the firewall or router.</small></div></div>
  : step === 4 ? <div className={styles.storageLayout}><div className={styles.field}><label>Managed server location</label><div className={styles.inline}><TextInput value={(destination?.path ?? instanceRoot) || 'ChunkPilot managed servers'} readOnly /><Button icon={<FolderOpen size={14} />} onClick={chooseFolder}>Choose parent folder</Button></div><small>{destination?.message ?? 'Enter a server name to establish the exact destination.'}</small>{destination && !destination.available && <p className={styles.error}>{destination.message}</p>}</div><section className={styles.worldSource} aria-labelledby="world-source-heading"><header><div><strong id="world-source-heading">Starting world</strong><p>Start with a new world or import your own.</p></div></header><div className={styles.worldChoices}><button type="button" aria-pressed={worldMode === 'New'} data-selected={worldMode === 'New'} onClick={() => { setWorldMode('New'); setWorldError(''); }}><span className={styles.worldChoiceIcon}><Plus size={17} /></span><span><strong>Create new world</strong><small>Minecraft generates a fresh world when this server starts.</small></span><span className={styles.radio} /></button><button type="button" aria-pressed={worldMode === 'Upload'} data-selected={worldMode === 'Upload'} disabled={worldUploadUnavailable} onClick={() => setWorldMode('Upload')}><span className={styles.worldChoiceIcon}><Archive size={17} /></span><span><strong>Upload World</strong><small>Copy one existing world from a folder or ZIP. The source stays unchanged.</small></span><span className={styles.radio} /></button></div>{worldUploadUnavailable && <div className={styles.notice}><Info size={16} /><span>This complete imported server source already owns its world layout. To use a different world, create from a Minecraft version or provider pack instead.</span></div>}{worldMode === 'Upload' && !worldUploadUnavailable && <div className={styles.worldUpload}>{existingWorld ? <div className={styles.worldResult}><span><strong>{existingWorld.worldName}</strong><small>{existingWorld.displayName} · {existingWorld.fileCount.toLocaleString()} files · {formatBytes(existingWorld.expandedSizeBytes)}{existingWorld.includesNether || existingWorld.includesEnd ? ` · ${existingWorld.includesNether ? 'Nether' : ''}${existingWorld.includesNether && existingWorld.includesEnd ? ' + ' : ''}${existingWorld.includesEnd ? 'End' : ''} included` : ''}</small></span><StatusPill text="Reviewed locally" /><Button onClick={() => chooseWorld(existingWorld.kind === 'Folder' ? 'folder' : 'zip')}>Replace</Button></div> : <><p>Choose a world folder or ZIP. Nether and End folders alongside the world are included.</p><div className={styles.worldActions}><Button variant="primary" icon={<FolderOpen size={14} />} onClick={() => chooseWorld('folder')}>Choose world folder</Button><Button icon={<Archive size={14} />} onClick={() => chooseWorld('zip')}>Choose world ZIP</Button></div></>}{worldError && <p className={styles.error}>{worldError}</p>}<details><summary>Import details</summary><small>Choose a source with one main <code>level.dat</code>. Ambiguous worlds, links, unsafe ZIP paths, and oversized archives are rejected. A stale <code>session.lock</code> is not copied.</small></details></div>}</section></div>
  : step === 5 ? <div className={styles.choices}>{[
    ['ThisComputerOnly', 'This computer only', 'Bind the server to 127.0.0.1 so other devices cannot connect. Best for isolated testing before you choose wider access.', HardDrive],
    ['HomeNetwork', 'LAN', 'People on the same Wi-Fi or wired network can join. Windows may still ask for approval.', Wifi],
    ['FriendsOverInternet', 'Internet', 'Host for friends outside your home after deliberate Windows and router setup. Nothing is exposed now; an optional point-in-time diagnostic remains available later.', Globe2]
  ].map(([id, title, detail, Icon]) => <button key={id as string} className={styles.choice} data-selected={networking === id} onClick={() => setNetworking(id as string)}><span className={styles.choiceIcon}><Icon size={18} /></span><span><strong>{title as string}</strong><p>{detail as string}</p></span><span className={styles.radio} /></button>)}</div>
  : operationId ? <div className={styles.operationProgress} aria-live="polite">
      <div className={styles.operationHeading}><span><strong>{progress?.isTerminal ? (progress.success ? 'Created' : 'Creation needs attention') : formatCreationStage(progress?.stage)}</strong><small>{progress?.message ?? 'ChunkPilot is continuing this operation. You may leave this view without stopping it.'}</small></span><strong>{!progress?.isIndeterminate && typeof progress?.percent === 'number' ? `${Math.round(progress.percent)}%` : progress?.isTerminal ? 'Stopped' : 'Working'}</strong></div>
      {!progress?.isTerminal && <div className={styles.operationTrack} role="progressbar" aria-label="Server creation progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={!progress?.isIndeterminate && typeof progress?.percent === 'number' ? Math.max(0, Math.min(100, progress.percent)) : undefined} data-indeterminate={progress?.isIndeterminate || typeof progress?.percent !== 'number'}><span style={{ width: `${progress?.isIndeterminate ? 18 : Math.max(2, Math.min(100, progress?.percent ?? 18))}%` }} /></div>}
      <div className={styles.operationMeta}><span>{progress?.lastMeaningfulStatus || (progress?.phase ? formatCreationStage(progress.phase) : 'Preparing the exact server files')}</span><span>{typeof progress?.stageElapsedSeconds === 'number' ? `Startup elapsed ${Math.floor(progress.stageElapsedSeconds / 60)}m ${Math.floor(progress.stageElapsedSeconds % 60)}s` : formatTransfer(progress)}</span></div>
      {typeof progress?.secondsSinceMeaningfulUpdate === 'number' && !progress.isTerminal && <p>Last startup milestone {Math.floor(progress.secondsSinceMeaningfulUpdate)}s ago. {progress.newLogOutputObserved ? 'New log output received; this does not necessarily mean startup advanced.' : 'Waiting for new server output.'}</p>}
      {isProgressStalled(progress) && <p className={styles.stalled}>No new startup milestone has been confirmed recently. Repeated log output does not extend the startup deadline. You can keep waiting or cancel safely.</p>}
      {progress?.error && <p className={styles.error}>{progress.error}</p>}
      {(progress?.recentStatus || progress?.currentArtifact) && <details className={styles.advanced}><summary>Technical details</summary><p>{progress.recentStatus || progress.currentArtifact}</p></details>}
      {(progress?.retainedInputBytes ?? 0) > 0 && <p>{formatBytes(progress!.retainedInputBytes!)} of verified input retained. Retry rechecks it; Discard removes only this temporary download.</p>}
      <div className={styles.recoveryActions}>
        {progress?.canRetry && <Button disabled={recoveryBusy} onClick={() => void recoverCreation('creation.retry')}>Retry from verified input</Button>}
        {progress?.canDiscard && <Button variant="danger" disabled={recoveryBusy} onClick={() => void recoverCreation('creation.discard')}>Discard retained download</Button>}
        {progress?.statusUnavailable && <Button onClick={() => setRecoveryRevision(value => value + 1)}>Retry status check</Button>}
        {onActivity && <Button variant="subtle" onClick={onActivity}>View Activity</Button>}
      </div>
    </div>
  : <><div className={styles.review}>{[
    ['Game', platform === 'Vanilla' ? 'Vanilla Minecraft' : isLoaderPlatform(platform) ? loaderTitle(platform) : platform], ['Version', platform === 'Modpack' && modpack ? modpack.kind === 'remote' ? `${modpack.project.name} · ${modpack.release.versionName} · Minecraft ${modpack.release.minecraftVersion} · ${modpack.release.loader}${modpack.release.loaderVersion ? ` ${modpack.release.loaderVersion}` : ''}` : `${modpack.local.inspection?.name} · ${modpack.local.inspection?.sourceKind} · Minecraft ${modpack.local.inspection?.minecraftVersion}` : selectedVersion ? platform === 'Paper' ? `Minecraft ${selectedVersion.id} · ${selectedPaperBuild ? `Paper build ${selectedPaperBuild.id}` : 'build not selected'}` : isLoaderPlatform(platform) ? `Minecraft ${selectedVersion.id} · ${selectedLoaderBuild ? `${loaderTitle(platform)} ${selectedLoaderBuild.loaderVersion}` : 'loader not selected'}` : `${selectedVersion.label} · ${selectedVersion.support}` : 'Not selected'], ['Memory', `${formatMemory(ramMb)} maximum · ${formatMemory(initialRamMb)} initial`], ['Server name', name || 'Not entered'], ['Destination', destination?.path ?? 'Not established'], ['World', worldMode === 'Upload' && existingWorld ? `${existingWorld.worldName} · copied from ${existingWorld.kind === 'Folder' ? 'folder' : 'ZIP'} · source preserved` : 'Create new world on first start'], ['Port', String(port)], ['Connectivity', networking === 'ThisComputerOnly' ? 'This computer only · bound to 127.0.0.1' : networking === 'FriendsOverInternet' ? 'Internet hosting guidance after creation' : 'LAN'], ['Java', platform === 'Modpack' && modpack ? `Java ${modpack.kind === 'remote' ? modpack.release.requiredJavaMajor : modpack.local.inspection?.requiredJavaMajor} · exact pack requirement` : selectedVersion?.javaMajor ? `Java ${selectedVersion.javaMajor} · compatibility policy` : 'Not established'], ['Launch', platform === 'Modpack' ? modpack?.kind === 'local' ? `${modpack.local.managementMode === 'ByReference' ? 'By reference' : 'Managed copy'} · ${modpack.local.launchRelativePath || 'reviewed launcher'}` : 'Verified pack files · exact declared loader · runtime validation on first start' : platform === 'Paper' ? 'Managed Paper dedicated server · plugins available after creation' : isLoaderPlatform(platform) ? `Managed ${loaderTitle(platform)} dedicated server · mods available after creation` : selectedVersion?.launchProfile.kind === 'ModernEulaNogui' ? 'Managed dedicated server' : selectedVersion?.launchProfile.kind ?? 'Not established']
  ].concat(modpack?.kind === 'remote' ? [['Pack installation', modpackRouteLabel(modpack.release)]] : []).concat(legacyArtifact ? [['Server files', `${legacyArtifact.fileName} · user supplied · ${legacyArtifact.sha256.slice(0, 12)}…`]] : []).map(([label, value]) => <div className={styles.reviewRow} key={label}><span>{label}</span><strong>{value}</strong></div>)}</div>{requiresExperimentalAck && <label className={styles.warningConsent}><input type="checkbox" checked={experimentalAccepted} onChange={event => setExperimentalAccepted(event.target.checked)} /><span>I understand this exact {platform === 'Modpack' ? 'pack release will be validated on demand during creation' : legacyArtifact ? 'user-supplied historical server JAR has not been certified by ChunkPilot and its identity is not inferred from its filename' : 'build has not been runtime-certified by ChunkPilot'}. I will use a new or isolated world until I have verified it.</span></label>}<label className={styles.eula}><input type="checkbox" checked={eula} onChange={event => setEula(event.target.checked)} /><span>I have read and accept the <strong>Minecraft EULA</strong>. ChunkPilot writes <code>eula.txt</code> only after this deliberate acceptance.</span></label></>;
  return <div className={styles.wrap}><header className={styles.header}><h1>Create server</h1></header><div className={styles.layout}><nav className={styles.steps} aria-label="Creation stages">{stages.map(([label], index) => <button key={label} className={styles.step} aria-label={`Step ${index + 1}: ${label}`} aria-current={step === index ? 'step' : undefined} data-current={step === index} data-complete={step > index} onClick={() => { if (index <= step || index < 6) goToStep(index); }}><span className={styles.number}>{step > index ? <Check size={12} /> : index + 1}</span><span className={styles.stepLabel}>{label}</span></button>)}</nav><section className={styles.stage}><header className={styles.stageHead}><div className={styles.eyebrow}>Step {step + 1} of {stages.length}</div><h2>{stages[step][0]}</h2><p>{step === 1 && platform === 'Modpack' ? 'Choose a modpack' : stages[step][1]}</p></header><div className={styles.body}>{body}</div><footer className={styles.footer}><Button variant="subtle" icon={<ChevronLeft size={14} />} disabled={step === 0 || submitted} onClick={() => goToStep(step - 1)}>Back</Button><div>{operationId ? <>{!progress?.isTerminal && <Button variant="danger" onClick={cancelCreation}>Cancel creation</Button>}<Button variant="primary" onClick={onDone}>View servers</Button></> : step < 6 ? <Button variant="primary" disabled={!canNext} onClick={() => goToStep(step + 1)}>Continue <ChevronRight size={14} /></Button> : <Button variant="primary" disabled={!canNext || submitted} icon={submitted ? <SlidersHorizontal size={14} /> : <Check size={14} />} onClick={create}>{submitted ? 'Submitting…' : 'Create server'}</Button>}</div></footer></section></div></div>;
}

function formatTransfer(progress: CreationProgress | null): string {
  if (!progress) return 'Waiting for authoritative progress';
  const downloaded = progress.bytesDownloaded ?? 0;
  const total = progress.totalBytes ?? 0;
  const speed = progress.bytesPerSecond ?? 0;
  if (downloaded <= 0 && speed <= 0) return progress.updatedAtUtc ? `Updated ${new Date(progress.updatedAtUtc).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', second: '2-digit' })}` : 'Preparing';
  const transfer = total > 0 ? `${formatBytes(downloaded)} of ${formatBytes(total)}` : formatBytes(downloaded);
  return speed > 0 ? `${transfer} · ${formatBytes(speed)}/s` : transfer;
}

function formatCreationStage(stage: string | undefined): string {
  if (!stage) return 'Creation accepted';
  const words = stage
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .toLowerCase();
  return words.replace(/^./, value => value.toUpperCase());
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${Math.max(0, Math.round(bytes))} B`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit += 1; }
  return `${value >= 100 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`;
}

function isProgressStalled(progress: CreationProgress | null): boolean {
  if (!progress?.isTerminal && typeof progress?.secondsSinceMeaningfulUpdate === 'number')
    return progress.secondsSinceMeaningfulUpdate >= 90;
  if (!progress?.updatedAtUtc || progress.isTerminal) return false;
  const updated = Date.parse(progress.updatedAtUtc);
  if (!Number.isFinite(updated)) return false;
  const stage = progress.stage?.toLowerCase() ?? '';
  const threshold = stage.includes('downloading') ? 120_000
    : stage.includes('activating') || stage.includes('registering') ? 300_000
      : stage.includes('preparing') ? 180_000 : 90_000;
  return Date.now() - updated > threshold;
}

function PaperBuildPicker({ catalog, value, error, onChange }: {
  catalog: PaperBuildCatalog | null;
  value: number | null;
  error: string;
  onChange: (id: number) => void;
}) {
  if (error) return <div className={styles.buildPanel}><strong>Paper build</strong><p className={styles.error}>{error}</p></div>;
  if (!catalog) return <div className={styles.buildPanel}><strong>Paper build</strong><p className={styles.loading}>Loading exact builds from PaperMC…</p></div>;
  const selectable = catalog.builds.filter(build => build.selectable);
  return <section className={styles.buildPanel} aria-labelledby="paper-build-heading">
    <header><div><strong id="paper-build-heading">Exact Paper build</strong><p>Stable builds are recommended. Pre-stable builds remain Experimental and need explicit acknowledgement. ChunkPilot verifies PaperMC's SHA-256 before activation.</p></div><span>{selectable.length} builds</span></header>
    <div className={styles.buildList} role="radiogroup" aria-label="Paper builds">
      {selectable.slice(0, 24).map(build => <button type="button" key={build.id} role="radio" aria-checked={value === build.id} data-selected={value === build.id || undefined} onClick={() => onChange(build.id)}>
        <span><strong>{build.label} · {build.channel} · {build.support}</strong><small>{build.publishedAt ? new Date(build.publishedAt).toLocaleDateString() : 'Publish date unavailable'} · {build.sizeBytes ? `${(build.sizeBytes / 1024 / 1024).toFixed(1)} MB` : 'Size unavailable'}</small></span>
        <span className={styles.radio} />
      </button>)}
    </div>
    {!selectable.length && <p className={styles.error}>PaperMC currently publishes no integrity-verifiable build for this Minecraft version.</p>}
    <small>{catalog.fromCache ? catalog.stale ? 'Saved PaperMC catalog · refresh required' : 'Saved PaperMC catalog' : 'Official PaperMC Fill v3 metadata'}</small>
  </section>;
}

function LoaderBuildPicker({ platform, catalog, value, error, resolvingId, onChange }: {
  platform: LoaderPlatform; catalog: LoaderBuildCatalog | null; value: string; error: string;
  resolvingId: string; onChange: (id: string) => void;
}) {
  const [search, setSearch] = useState(''); const [pageIndex, setPageIndex] = useState(0);
  useEffect(() => { setSearch(''); setPageIndex(0); }, [platform, catalog?.minecraftVersion]);
  const heading = `Exact ${loaderTitle(platform)} version`;
  if (!catalog) return <div className={styles.buildPanel}><strong>{heading}</strong>
    <p className={error ? styles.error : styles.loading} role={error ? 'alert' : 'status'}>{error || 'Loading exact official versions…'}</p></div>;
  const selectable = catalog.builds.filter(build => build.selectable);
  const filtered = catalog.builds.filter(build => `${build.label} ${build.loaderVersion}`.toLowerCase().includes(search.toLowerCase().trim()));
  const pageSize = 48; const pages = Math.max(1, Math.ceil(filtered.length / pageSize));
  const currentPage = Math.min(pageIndex, pages - 1);
  return <section className={styles.buildPanel} aria-labelledby="loader-build-heading">
    <header><div><strong id="loader-build-heading">{heading}</strong><p>{platform === 'Fabric'
      ? 'Exact Minecraft, Loader, and server launcher versions.'
      : platform === 'LegacyFabric' || platform === 'Ornithe'
        ? 'Historical inventory only. Creation is not available yet.'
        : 'Choose an official build. Older builds are verified when selected.'}</p></div><span>{catalog.builds.length} builds</span></header>
    {catalog.builds.length > 12 && <SearchInput aria-label={`Search exact ${platform} versions`} placeholder="Find a loader version"
      value={search} onChange={event => { setSearch(event.target.value); setPageIndex(0); }} />}
    {error && <p className={styles.error} role="alert">{error}</p>}
    <div className={styles.buildList} role="radiogroup" aria-label={`${platform} versions`}>
      {filtered.slice(currentPage * pageSize, (currentPage + 1) * pageSize).map(build => <button type="button" key={build.id}
        role="radio" aria-checked={value === build.id} data-selected={value === build.id || undefined}
        disabled={resolvingId === build.id || (!build.selectable && !build.canResolveIntegrity)}
        title={!build.selectable ? build.supportReason : undefined} onClick={() => onChange(build.id)}>
        <span><strong>{build.label} · {build.channel} · {build.canResolveIntegrity && !build.selectable ? 'Needs verification' : build.support}</strong>
          <small role={resolvingId === build.id ? 'status' : undefined}>{resolvingId === build.id ? 'Verifying official checksum…'
            : platform === 'Fabric' ? `Server launcher installer ${build.installerVersion}`
              : build.hasIntegrityMetadata ? 'Official provider checksum'
                : build.canResolveIntegrity ? 'Select to verify checksum' : 'Provider checksum unavailable'}</small></span>
        <span className={styles.radio} />
      </button>)}
    </div>
    {!filtered.length && <p role="status">No matching loader versions.</p>}
    {pages > 1 && <div className={styles.buildPagination}><span>Page {currentPage + 1} of {pages}</span>
      <Button variant="subtle" disabled={currentPage === 0} onClick={() => setPageIndex(currentPage - 1)}>Previous</Button>
      <Button variant="subtle" disabled={currentPage + 1 === pages} onClick={() => setPageIndex(currentPage + 1)}>Next</Button></div>}
    {!selectable.length && !catalog.builds.some(build => build.canResolveIntegrity) && <p className={styles.error}>No verified build is available for this Minecraft version.</p>}
    <small>{catalog.fromCache ? catalog.stale ? `Saved ${platform} catalog · refresh required` : `Saved ${platform} catalog` : `Official ${platform} metadata`}</small>
  </section>;
}
