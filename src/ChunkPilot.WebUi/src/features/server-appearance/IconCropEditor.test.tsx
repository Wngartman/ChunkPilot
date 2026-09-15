// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useAppStore } from '../../state/store';
import type { BridgeAdapter } from '../../bridge/client';
import { IconCropEditor } from './IconCropEditor';
import { defaultIconRecipe } from './iconCrop';

const source = { token: 'native-token', sourceUrl: 'data:image/png;base64,c291cmNl', fileName: 'Original',
  recipe: { ...defaultIconRecipe, zoom: 2, brightness: 1.2 }, originalAvailable: true,
  detail: 'Editing from the preserved bounded original; earlier effects are not compounded.' };
const request = vi.fn(async (_method: string) => source);

beforeEach(() => {
  request.mockReset(); request.mockResolvedValue(source);
  const bridge = { request, subscribe: () => () => undefined, dispose: () => undefined } as unknown as BridgeAdapter;
  useAppStore.setState({ bridge, busy: new Set(), error: null });
  window.history.replaceState({}, '', '/');
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(() => ({
    translate() {}, rotate() {}, drawImage() {}, clearRect() {}, filter: '', imageSmoothingEnabled: false
  }) as unknown as CanvasRenderingContext2D);
  vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockReturnValue('data:image/png;base64,Y3JvcA==');
  vi.stubGlobal('Image', class {
    naturalWidth = 256; naturalHeight = 256; onload: (() => void) | null = null;
    set src(_value: string) { queueMicrotask(() => this.onload?.()); }
  });
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe('existing icon editing', () => {
  it('opens the current native source and stages a recipe without saving or replacing the source', async () => {
    const changed = vi.fn();
    render(<IconCropEditor serverId="server-one" serverName="First" savedIconUrl="data:image/png;base64,c2F2ZWQ=" stagedIconUrl={null} onStagedIcon={changed} />);
    fireEvent.click(screen.getByRole('button', { name: 'Edit server icon' }));
    expect(await screen.findByRole('button', { name: 'Use this crop' })).toBeTruthy();
    expect(request).toHaveBeenCalledWith('appearance.editIcon', { serverId: 'server-one' }, undefined);
    expect((screen.getByRole('slider', { name: 'Zoom' }) as HTMLInputElement).value).toBe('2');
    expect(changed).not.toHaveBeenCalled();
    fireEvent.change(screen.getByRole('slider', { name: 'Brightness' }), { target: { value: '1.4' } });
    fireEvent.click(screen.getByRole('button', { name: 'Use this crop' }));
    expect(changed).toHaveBeenCalledWith('data:image/png;base64,Y3JvcA==', { token: 'native-token', recipe: { ...source.recipe, brightness: 1.4 } });
    expect(request.mock.calls.some(args => String(args[0]).startsWith('settings.'))).toBe(false);
  });

  it('resets adjustments against the original and cancel makes no staged change', async () => {
    const changed = vi.fn();
    render(<IconCropEditor serverId="server-one" serverName="First" savedIconUrl="saved" stagedIconUrl={null} onStagedIcon={changed} />);
    fireEvent.click(screen.getByRole('button', { name: 'Edit server icon' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Reset crop and adjustments' }));
    expect((screen.getByRole('slider', { name: 'Zoom' }) as HTMLInputElement).value).toBe('1');
    expect((screen.getByRole('slider', { name: 'Brightness' }) as HTMLInputElement).value).toBe('1');
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('button', { name: 'Use this crop' })).toBeNull();
    expect(changed).not.toHaveBeenCalled();
  });

  it('does not let a late source from the old server open on a different server', async () => {
    let complete!: (value: typeof source) => void;
    request.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
    const changed = vi.fn();
    const { rerender } = render(<IconCropEditor serverId="server-one" serverName="First" savedIconUrl="saved" stagedIconUrl={null} onStagedIcon={changed} />);
    fireEvent.click(screen.getByRole('button', { name: 'Replace image' }));
    rerender(<IconCropEditor serverId="server-two" serverName="Second" savedIconUrl="saved-two" stagedIconUrl={null} onStagedIcon={changed} />);
    await act(async () => complete(source));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Use this crop' })).toBeNull());
    expect(changed).not.toHaveBeenCalled();
  });
});
