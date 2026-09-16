import { Component, type ReactNode } from 'react';
import { Button, EmptyState } from '../design-system/Primitives';

/** Keep native window controls and navigation available if an individual view cannot render. */
export class ViewErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() { return { failed: true }; }

  render() {
    if (!this.state.failed) return this.props.children;
    return <div role="alert"><EmptyState title="This view couldn’t open"
      detail="Use the sidebar to open another view, or try again."
      action={<Button onClick={() => this.setState({ failed: false })}>Try again</Button>} /></div>;
  }
}
