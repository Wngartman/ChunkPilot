// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryControl } from './MemoryControl';

afterEach(cleanup);

describe('Memory control', () => {
  it('moves from a preset to exact custom entry without changing the value prematurely', () => {
    const changed = vi.fn();
    render(<MemoryControl valueMib={4096} onChange={changed} />);
    fireEvent.change(screen.getByLabelText('Memory'), { target: { value: 'custom' } });
    expect(screen.getByLabelText('Custom memory amount')).toBeTruthy();
    expect(changed).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText('Custom memory amount'), { target: { value: '6.5 GB' } });
    fireEvent.keyDown(screen.getByLabelText('Custom memory amount'), { key: 'Enter' });
    expect(changed).toHaveBeenLastCalledWith(6656);
  });

  it('moves from a custom value back to a preset exactly', () => {
    const changed = vi.fn();
    render(<MemoryControl valueMib={4710} onChange={changed} />);
    fireEvent.change(screen.getByLabelText('Memory'), { target: { value: '8192' } });
    expect(changed).toHaveBeenLastCalledWith(8192);
  });

  it('returns to preset mode when discard restores a preset value', () => {
    const changed = vi.fn();
    const view = render(<MemoryControl valueMib={4710} onChange={changed} />);
    expect(screen.getByLabelText('Custom memory amount')).toBeTruthy();
    view.rerender(<MemoryControl valueMib={4096} onChange={changed} />);
    expect(screen.queryByLabelText('Custom memory amount')).toBeNull();
    expect((screen.getByLabelText('Memory') as HTMLSelectElement).value).toBe('4096');
  });

  it('announces invalid custom input and does not silently clamp it', () => {
    const changed = vi.fn();
    render(<MemoryControl valueMib={4096} onChange={changed} />);
    fireEvent.change(screen.getByLabelText('Memory'), { target: { value: 'custom' } });
    fireEvent.change(screen.getByLabelText('Custom memory amount'), { target: { value: '0' } });
    fireEvent.blur(screen.getByLabelText('Custom memory amount'));
    expect(screen.getByRole('alert').textContent).toContain('greater than zero');
    expect(changed).not.toHaveBeenCalled();
  });
});
