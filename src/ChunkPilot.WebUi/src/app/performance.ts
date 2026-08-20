export function runMeasuredNavigation(label: string, action: () => void) {
  const started = globalThis.performance.now();
  action();
  globalThis.requestAnimationFrame(() => {
    document.documentElement.dataset.cpLastNavigation = label;
    document.documentElement.dataset.cpLastNavigationMs = (globalThis.performance.now() - started).toFixed(1);
  });
}
