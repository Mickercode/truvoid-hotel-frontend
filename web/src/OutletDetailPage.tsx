import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from './api'
import { ConfirmButton } from './ConfirmButton'

type Outlet = { id: string; name: string; status: string; walletId: string; createdAt: string; balanceKobo: number }
type Call = { id: string; verificationType: string; status: string; verdict?: string | null; fullName?: string | null; createdAt: string; environment?: string | null }
type History = { page: number; pageSize: number; items: Call[] }

const money = (kobo: number) => `₦${(kobo / 100).toLocaleString('en-NG', { minimumFractionDigits: 2 })}`

/**
 * Agency-admin view of a single outlet: its own wallet, its verification activity
 * (scoped by outletId), and suspend/reactivate. Outlet users reach the same activity
 * through their own RLS-scoped History page.
 */
export function OutletDetailPage() {
  const { outletId = '' } = useParams()
  const [outlet, setOutlet] = useState<Outlet | null>(null)
  const [calls, setCalls] = useState<Call[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [actionError, setActionError] = useState('')

  async function load() {
    setLoading(true)
    setError('')
    try {
      const [detail, history] = await Promise.all([
        api.get<Outlet>(`/v1/tenant/outlets/${outletId}`),
        api.get<History>(`/v1/tenant/verification-calls?outletId=${outletId}&page=1&pageSize=25`),
      ])
      setOutlet(detail)
      setCalls(history.items)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'This outlet could not be loaded.')
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => { void load() }, [outletId])

  async function changeStatus(action: 'suspend' | 'reactivate') {
    if (busy) return
    setBusy(true)
    setActionError('')
    try {
      await api.post(`/v1/tenant/outlets/${outletId}/${action}`, {})
      await load()
    } catch (reason) {
      setActionError(reason instanceof Error ? reason.message : 'The status change failed.')
    } finally {
      setBusy(false)
    }
  }

  if (loading) return <section><div className="empty">Loading outlet…</div></section>
  if (error || !outlet)
    return (
      <section>
        <div className="empty error" role="alert">
          {error || 'Outlet not found.'}
          <br />
          <button className="retry-button" onClick={() => void load()}>Retry</button>
        </div>
      </section>
    )

  return (
    <section>
      <div className="page-title">
        <div className="eyebrow"><Link className="text-link" to="/outlets">OUTLETS</Link> / DETAIL</div>
        <h1>{outlet.name}</h1>
        <p className="lede">Outlet activity, wallet, and status. Suspend an outlet to stop its verifications without affecting the agency.</p>
      </div>
      <div className="wallet-hero">
        <div>
          <span className="stat-label">OUTLET WALLET</span>
          <strong>{money(outlet.balanceKobo)}</strong>
          <span className="stat-note">Status: {outlet.status}</span>
        </div>
        {outlet.status === 'suspended' ? (
          <button className="button button-primary" disabled={busy} onClick={() => void changeStatus('reactivate')}>
            {busy ? 'Working…' : 'Reactivate outlet'}
          </button>
        ) : (
          <ConfirmButton
            label="Suspend outlet"
            question="Suspend this outlet? Its verifications stop."
            confirmLabel="Suspend outlet"
            className="button button-primary"
            disabled={busy}
            onConfirm={() => void changeStatus('suspend')}
          />
        )}
      </div>
      {actionError && <div className="notice error" role="alert">{actionError}</div>}
      <div className="section-heading">
        <div><div className="eyebrow">ACTIVITY</div><h2>Recent verifications</h2></div>
      </div>
      {calls.length ? (
        <div className="table-wrap">
          <table>
            <thead>
              <tr><th>Type</th><th>Result</th><th>Name</th><th>Reference</th><th>Created</th></tr>
            </thead>
            <tbody>
              {calls.map((call) => (
                <tr key={call.id}>
                  <td>{call.verificationType.toUpperCase()}</td>
                  <td><span className={`badge ${call.status}`}>{call.verdict ?? call.status}</span></td>
                  <td>{call.fullName ?? <span className="muted">—</span>}</td>
                  <td><code>{call.id.slice(0, 12)}</code></td>
                  <td>{new Date(call.createdAt).toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="empty">No verifications from this outlet yet.</div>
      )}
    </section>
  )
}
