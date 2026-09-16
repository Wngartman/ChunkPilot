export function versionHealthLabel(health: string): string {
  switch (health) {
    case 'Healthy': return 'Healthy';
    case 'PendingValidation': return 'Pending validation';
    case 'Failed': return 'Failed';
    case 'RolledBack': return 'Rolled back';
    default: return 'Health not recorded';
  }
}

export function missingInstalledBuildTitle(platform: string, version?: string): string {
  return version?.trim()
    ? `${platform === 'Paper' ? 'Paper build' : platform} ${version} is not in the current inventory`
    : `${platform} version not identified`;
}
