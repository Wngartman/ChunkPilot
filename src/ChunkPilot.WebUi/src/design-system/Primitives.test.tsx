// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ActionMenu } from './ActionMenu';
import { Combobox, ConfirmDialog, Dialog } from './Primitives';
import { useState } from 'react';

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

it('wraps exact identities without widening ordinary selectors and keeps the popup inside the viewport', async () => {
  const user = userEvent.setup();
  const label = 'An unusually long pack release 4.2.0 · Minecraft 1.21.8 · NeoForge';
  render(<Combobox value="exact" options={[{ value: 'exact', label }]} ariaLabel="Exact release" onChange={() => undefined} wrapLabels />);
  const trigger = screen.getByRole('combobox');
  expect(trigger.getAttribute('data-wrap')).toBe('true');
  await user.click(trigger);
  const option = await screen.findByRole('option', { name: label });
  const popup = option.closest('[data-wrap="true"]') as HTMLElement;
  expect(popup).toBeTruthy();
  expect(Number.parseFloat(popup.style.width)).toBeLessThanOrEqual(window.innerWidth - 16);
  expect(Number.parseFloat(popup.style.width)).toBeGreaterThanOrEqual(380);
});

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
  it('keeps spaces in multi-word search and starts keyboard navigation from the selected value', async () => {
    const user = userEvent.setup(); const selected = vi.fn();
    render(<Combobox value="atm" ariaLabel="Modpack" searchable onChange={selected}
      options={[{ value: 'first', label: 'Copper Valley' }, { value: 'atm', label: 'All the Mods' }]} />);
    await user.click(screen.getByRole('combobox', { name: 'Modpack' }));
    const search = screen.getByRole('searchbox', { name: 'Search modpack' });
    await user.type(search, 'All the');
    expect((search as HTMLInputElement).value).toBe('All the');
    expect(selected).not.toHaveBeenCalled();
    expect(screen.getByRole('option', { name: 'All the Mods' })).toBeTruthy();
    await user.keyboard('{ArrowDown}{Enter}');
    expect(selected).toHaveBeenCalledWith('atm');
  });

  it('opens on the selected option rather than silently resetting to the first', async () => {
    const user = userEvent.setup(); const selected = vi.fn();
    render(<Combobox value="beta" ariaLabel="Channel" onChange={selected}
      options={[{ value: 'release', label: 'Release' }, { value: 'beta', label: 'Beta' }, { value: 'alpha', label: 'Alpha' }]} />);
    await user.click(screen.getByRole('combobox', { name: 'Channel' }));
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('option', { name: 'Beta' })));
    await user.keyboard('{End}{Enter}');
    expect(selected).toHaveBeenCalledWith('alpha');
  });

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

describe('Dialog focus', () => {
  it('keeps focus in the edited field across renders and restores its opener', async () => {
    const user = userEvent.setup();
    function Example() {
      const [open, setOpen] = useState(false); const [value, setValue] = useState('');
      return <><button onClick={() => setOpen(true)}>Open editor</button>
        <Dialog open={open} title="Edit details" onClose={() => setOpen(false)} footer={<button onClick={() => setOpen(false)}>Done</button>}>
          <input aria-label="First field" /><input aria-label="Second field" value={value} onChange={event => setValue(event.target.value)} />
        </Dialog></>;
    }
    render(<Example />);
    const opener = screen.getByRole('button', { name: 'Open editor' });
    await user.click(opener);
    const second = screen.getByRole('textbox', { name: 'Second field' });
    await user.type(second, 'long server name');
    expect((second as HTMLInputElement).value).toBe('long server name');
    expect(document.activeElement).toBe(second);
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(opener);
  });

  it('portals confirmations, starts on Cancel and traps keyboard focus', async () => {
    const user = userEvent.setup(); const confirm = vi.fn();
    const { container } = render(<ConfirmDialog open title="Delete fixture?" detail="The selected fixture only."
      confirmLabel="Delete" destructive onConfirm={confirm} onCancel={() => undefined} />);
    const dialog = screen.getByRole('alertdialog');
    expect(container.contains(dialog)).toBe(false);
    const cancel = screen.getByRole('button', { name: 'Cancel' });
    await waitFor(() => expect(document.activeElement).toBe(cancel));
    await user.tab({ shift: true });
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Delete' }));
    await user.tab();
    expect(document.activeElement).toBe(cancel);
    expect(confirm).not.toHaveBeenCalled();
  });
});
