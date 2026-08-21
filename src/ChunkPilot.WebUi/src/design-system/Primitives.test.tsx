// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ActionMenu } from './ActionMenu';
import { Combobox } from './Primitives';

class ResizeObserverStub {
  observe() { }
  unobserve() { }
  disconnect() { }
}

Object.defineProperty(window, 'ResizeObserver', { value: ResizeObserverStub, writable: true });
Object.defineProperty(window, 'PointerEvent', { value: MouseEvent, writable: true });
Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { value: () => undefined, writable: true });
Object.defineProperty(HTMLElement.prototype, 'hasPointerCapture', { value: () => false, writable: true });
Object.defineProperty(HTMLElement.prototype, 'setPointerCapture', { value: () => undefined, writable: true });
Object.defineProperty(HTMLElement.prototype, 'releasePointerCapture', { value: () => undefined, writable: true });

afterEach(cleanup);

describe('ActionMenu', () => {
  it('portals its content, supports keyboard escape, and restores trigger focus', async () => {
    const user = userEvent.setup();
    const selected = vi.fn();
    const { container } = render(<ActionMenu label="Server actions" trigger={<span>•••</span>} items={[
      { label: 'Restart server', onSelect: selected },
      { label: 'Open server folder', onSelect: selected }
    ]} />);
    const trigger = screen.getByRole('button', { name: 'Server actions' });

    await user.click(trigger);

    const item = await screen.findByRole('menuitem', { name: 'Open server folder' });
    expect(item).toBeTruthy();
    expect(container.contains(item)).toBe(false);

    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('menuitem', { name: 'Open server folder' })).toBeNull());
    expect(document.activeElement).toBe(trigger);
    expect(selected).not.toHaveBeenCalled();
  });

  it('closes after selection and invokes only the selected action', async () => {
    const user = userEvent.setup();
    const restart = vi.fn();
    const openFolder = vi.fn();
    render(<ActionMenu label="Server actions" trigger={<span>•••</span>} items={[
      { label: 'Restart server', onSelect: restart },
      { label: 'Open server folder', onSelect: openFolder }
    ]} />);

    await user.click(screen.getByRole('button', { name: 'Server actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Open server folder' }));

    expect(openFolder).toHaveBeenCalledOnce();
    expect(restart).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole('menuitem', { name: 'Open server folder' })).toBeNull());
  });
});

describe('Combobox', () => {
  it('portals its listbox, selects by keyboard, and restores trigger focus', async () => {
    const user = userEvent.setup();
    const selected = vi.fn();
    const { container } = render(<Combobox value="release" ariaLabel="Version channel" onChange={selected}
      options={[{ value: 'release', label: 'Release' }, { value: 'beta', label: 'Beta' }, { value: 'alpha', label: 'Alpha' }]} />);
    const trigger = screen.getByRole('combobox', { name: 'Version channel' });

    await user.click(trigger);
    const listbox = await screen.findByRole('listbox', { name: 'Version channel' });
    expect(container.contains(listbox)).toBe(false);
    await user.keyboard('{ArrowDown}{Enter}');

    expect(selected).toHaveBeenCalledWith('beta');
    expect(screen.queryByRole('listbox', { name: 'Version channel' })).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('clamps the portal to the visible viewport', async () => {
    const user = userEvent.setup();
    render(<Combobox value="" ariaLabel="Minecraft version" onChange={() => undefined}
      options={[{ value: '', label: 'Any version' }, { value: 'b1.8.1', label: 'b1.8.1 · Beta' }]} />);
    const trigger = screen.getByRole('combobox', { name: 'Minecraft version' });
    vi.spyOn(trigger, 'getBoundingClientRect').mockReturnValue({
      x: 1010, y: 740, left: 1010, top: 740, right: 1080, bottom: 772, width: 70, height: 32,
      toJSON: () => ({})
    } as DOMRect);
    await user.click(trigger);
    const listbox = await screen.findByRole('listbox', { name: 'Minecraft version' });
    const popover = listbox.parentElement as HTMLElement;
    await waitFor(() => expect(Number.parseFloat(popover.style.left)).toBeGreaterThanOrEqual(8));
    expect(Number.parseFloat(popover.style.top)).toBeGreaterThanOrEqual(8);
    expect(Number.parseFloat(popover.style.maxHeight)).toBeGreaterThanOrEqual(96);
  });

  it('filters historical options without losing keyboard selection', async () => {
    const user = userEvent.setup(); const selected = vi.fn();
    render(<Combobox value="" ariaLabel="Minecraft version" searchable onChange={selected}
      options={[{ value: '', label: 'Any version' }, { value: '1.21.8', label: '1.21.8' }, { value: 'b1.8.1', label: 'b1.8.1 · Beta' }]} />);
    await user.click(screen.getByRole('combobox', { name: 'Minecraft version' }));
    await user.type(screen.getByRole('searchbox', { name: 'Search minecraft version' }), 'b1.8');
    expect(screen.queryByRole('option', { name: '1.21.8' })).toBeNull();
    await user.keyboard('{ArrowDown}{Enter}');
    expect(selected).toHaveBeenCalledWith('b1.8.1');
  });
});
