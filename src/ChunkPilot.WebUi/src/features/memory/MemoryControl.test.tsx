// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryControl } from './MemoryControl';

afterEach(cleanup);

describe('Memory control', () => {
  it('moves from a preset to exact custom entry without changing the value prematurely', () => {
    const changed = vi.fn();
    render(<MemoryControl valueMib={4096} onChange={changed} />);
    fireEvent.change(screen.getByLabelText('Memory'), { target: { value: 'custom' } });
    expect(screen.getByLabelText('Custom memory in gigabytes')).toBeTruthy();
    expect(changed).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText('Custom memory in gigabytes'), { target: { value: '6.5' } });
    fireEvent.keyDown(screen.getByLabelText('Custom memory in gigabytes'), { key: 'Enter' });
    expect(changed).toHaveBeenLastCalledWith(6656);
    expect(screen.getByText('6,656 MB')).toBeTruthy();
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
    expect(screen.getByLabelText('Custom memory in gigabytes')).toBeTruthy();
    view.rerender(<MemoryControl valueMib={4096} onChange={changed} />);
    expect(screen.queryByLabelText('Custom memory in gigabytes')).toBeNull();
    expect((screen.getByLabelText('Memory') as HTMLSelectElement).value).toBe('4096');
  });

  it('announces invalid custom input and does not silently clamp it', () => {
    const changed = vi.fn();
    render(<MemoryControl valueMib={4096} onChange={changed} />);
    fireEvent.change(screen.getByLabelText('Memory'), { target: { value: 'custom' } });
    fireEvent.change(screen.getByLabelText('Custom memory in gigabytes'), { target: { value: '0' } });
    fireEvent.blur(screen.getByLabelText('Custom memory in gigabytes'));
    expect(screen.getByRole('alert').textContent).toContain('greater than zero');
    expect(changed).not.toHaveBeenCalled();
  });

  it('offers a 10 GB preset', () => {
    const changed = vi.fn();
    render(<MemoryControl valueMib={4096} onChange={changed} />);
    const option = screen.getByRole('option', { name: '10,240 MB (10 GB)' });
    expect((option as HTMLOptionElement).value).toBe('10240');
    fireEvent.change(screen.getByLabelText('Memory'), { target: { value: '10240' } });
    expect(changed).toHaveBeenLastCalledWith(10240);
  });

  it('reports invalid input immediately and clears it on preset selection', () => {
    const valid = vi.fn();
    const changed = vi.fn();
    render(<MemoryControl valueMib={4096} onChange={changed} onValidityChange={valid} />);
    fireEvent.change(screen.getByLabelText('Memory'), { target: { value: 'custom' } });
    fireEvent.change(screen.getByLabelText('Custom memory in gigabytes'), { target: { value: '1,5' } });
    expect(valid).toHaveBeenLastCalledWith(false);
    expect(changed).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText('Memory'), { target: { value: '2048' } });
    expect(valid).toHaveBeenLastCalledWith(true);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('keeps typed custom text while synchronizing the latest valid numeric value', () => {
    const saved = vi.fn();
    function Controlled() {
      const [value, setValue] = useState(4096);
      return <><MemoryControl valueMib={value} onChange={setValue} /><button onClick={() => saved(value)}>Save</button></>;
    }
    render(<Controlled />);
    fireEvent.change(screen.getByLabelText('Memory'), { target: { value: 'custom' } });
    fireEvent.change(screen.getByLabelText('Custom memory in gigabytes'), { target: { value: '3.1416' } });
    expect((screen.getByLabelText('Custom memory in gigabytes') as HTMLInputElement).value).toBe('3.1416');
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(saved).toHaveBeenLastCalledWith(3217);
    fireEvent.blur(screen.getByLabelText('Custom memory in gigabytes'));
    fireEvent.blur(screen.getByLabelText('Custom memory in gigabytes'));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(saved).toHaveBeenLastCalledWith(3217);
  });

  it('revalidates an unchanged value when its maximum bound shrinks', () => {
    const valid = vi.fn();
    const view = render(<MemoryControl valueMib={2048} onChange={() => undefined} onValidityChange={valid} maximumMib={4096} />);
    expect(valid).toHaveBeenLastCalledWith(true);
    view.rerender(<MemoryControl valueMib={2048} onChange={() => undefined} onValidityChange={valid} maximumMib={1024} />);
    expect(valid).toHaveBeenLastCalledWith(false);
    expect(screen.getByRole('alert').textContent).toContain('cannot exceed');
  });
});
