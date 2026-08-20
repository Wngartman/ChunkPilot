// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ActionMenu } from './ActionMenu';

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
