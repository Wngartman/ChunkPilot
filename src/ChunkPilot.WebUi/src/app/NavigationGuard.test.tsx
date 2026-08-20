// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { NavigationGuardProvider, useGuardedNavigation, useUnsavedChangesGuard } from './NavigationGuard';

afterEach(cleanup);

function DirtyHarness({ discard, leave }: { discard: () => void; leave: () => void }) {
  const navigate = useGuardedNavigation();
  useUnsavedChangesGuard(true, discard, 'Server appearance has unsaved changes.');
  return <button onClick={() => navigate(leave)}>Open Console</button>;
}

describe('NavigationGuard', () => {
  it('keeps the user in place on cancel and discards before confirmed navigation', async () => {
    const user = userEvent.setup();
    const discard = vi.fn();
    const leave = vi.fn();
    render(<NavigationGuardProvider><DirtyHarness discard={discard} leave={leave} /></NavigationGuardProvider>);

    await user.click(screen.getByRole('button', { name: 'Open Console' }));
    expect(screen.getByRole('alertdialog').textContent).toContain('Server appearance has unsaved changes.');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(discard).not.toHaveBeenCalled();
    expect(leave).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Open Console' }));
    await user.click(screen.getByRole('button', { name: 'Discard and leave' }));
    expect(discard).toHaveBeenCalledOnce();
    expect(leave).toHaveBeenCalledOnce();
  });
});
