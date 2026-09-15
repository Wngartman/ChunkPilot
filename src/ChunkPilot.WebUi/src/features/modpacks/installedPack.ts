import type { ServerSummary, SemanticTone, UpdateSummary } from '../../bridge/types';

export function installedPackIdentity(pack: NonNullable<ServerSummary['modpack']>) {
  const provider = pack.provider === 'LocalPackageHistory' ? 'Local archive' : pack.provider;
  return {
    provider,
    name: pack.projectName.trim() || `${provider} project ${pack.projectId}`,
    release: pack.versionName.trim() || `File ${pack.versionId}`,
    labelsAvailable: Boolean(pack.projectName.trim() && pack.versionName.trim())
  };
}

export function installedPackUpdate(update: UpdateSummary, providerUpdates: boolean): {
  tone: SemanticTone; detail: string;
} {
  if (!providerUpdates) return { tone: 'neutral', detail: 'Provider updates are unavailable for this local archive.' };
  if (update.operationState && update.cancellable)
    return { tone: 'info', detail: update.operationDetail || update.detail || 'The native update operation is in progress.' };
  if (!update.checkedAt || update.status === 'Not checked')
    return { tone: 'neutral', detail: 'Check the provider to compare this exact installed release. No current update result is available.' };
  if (update.status === 'Up to date')
    return { tone: 'success', detail: update.detail || 'No newer compatible release was found in the last completed provider check.' };
  if (update.status.startsWith('Update available'))
    return { tone: 'warning', detail: update.detail || 'A newer release was found. Review its compatibility before installing.' };
  return { tone: 'warning', detail: update.detail || 'The provider check did not establish whether this installed release is current.' };
}
