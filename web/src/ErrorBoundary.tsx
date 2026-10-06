import { Component, ErrorInfo, ReactNode } from 'react'

/**
 * Catches render errors so the app shows a recoverable screen instead of a blank page.
 */
export class ErrorBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state: { error: Error | null } = { error: null }

  static getDerivedStateFromError(error: Error) {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Unhandled UI error:', error, info)
  }

  render() {
    if (!this.state.error) return this.props.children
    return (
      <div className="loading-screen">
        <img className="brand-logo" src="/TruvoID-logo.png" alt="TruvoID" width={44} height={44} />
        <h1 style={{ fontSize: 22 }}>Something went wrong.</h1>
        <p className="lede" style={{ maxWidth: 460, textAlign: 'center' }}>
          Reload the page to continue. If this keeps happening, contact TruvoID support.
        </p>
        <button className="button button-primary" onClick={() => window.location.reload()}>Reload</button>
      </div>
    )
  }
}
