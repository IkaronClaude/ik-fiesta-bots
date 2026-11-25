import { Component, type ReactNode } from 'react';

/** Keeps a render error in one view from blanking the whole console; shows it and offers a retry. */
export class ErrorBoundary extends Component<{ children: ReactNode; resetKey?: string }, { error: Error | null }> {
  state = { error: null as Error | null };
  static getDerivedStateFromError(error: Error) { return { error }; }
  componentDidUpdate(prev: { resetKey?: string }) { if (prev.resetKey !== this.props.resetKey && this.state.error) this.setState({ error: null }); }
  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div className="banner err" role="alert">
        <span>This view failed to render: {this.state.error.message}</span>
        <button className="btn sm" onClick={() => this.setState({ error: null })}>retry</button>
      </div>
    );
  }
}
