export function isFixtureMode(
  location: Pick<Location, 'search' | 'hostname'> = window.location,
  development = import.meta.env.DEV
): boolean {
  const query = new URLSearchParams(location.search);
  if (!query.has('fixture')) return false;
  const host = location.hostname.toLowerCase();
  if (host === 'fixture.chunkpilot.local') return true;
  return development && (host === '127.0.0.1' || host === 'localhost');
}
