import { useEffect, useState } from 'react'
import { api } from './api'

type Entry = {
  id: string
  occurredAt: string
  actorType: string
  actorEmail: string | null
  action: string
  entity: string
  entityId: string | null
  organizationName: string | null
  details: string | null
}
type AuditPage = { page: number; pageSize: number; items: Entry[] }

const ACTIONS = [
  'Created', 'Updated', 'Deleted', 'Verified', 'WalletCredited', 'WalletDebited',
  'WalletReversed', 'ApiKeyGenerated', 'ApiKeyRevoked', 'Login', 'Logout', 'Notified', 'RoleChanged',
]

/** Read-only platform activity log for staff. */
export function AdminAuditPage() {
  const [page, setPage] = useState(1)
  const [action, setAction] = useState('')
  const [entity, setEntity] = useState('')
  const [data, setData] = useState<AuditPage | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  async function load() {
    setLoading(true)
    setError('')
    const query = new URLSearchParams({ page: String(page), pageSize: '50' })
    if (action) query.set('action', action)
    if (entity.trim()) query.set('entity', entity.trim())
    try {
      setData(await api.get<AuditPage>(`/v1/admin/audit?${query.toString()}`))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'The activity log could not be loaded.')
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => { void load() }, [page, action])

  const hasMore = (data?.items.length ?? 0) >= (data?.pageSize ?? 50)

  return <section>
    <div className="page-title">
      <div className="eyebrow">ADMIN / ACTIVITY</div>
      <h1>Activity log.</h1>
      <p className="lede">Every recorded action across the platform, newest first.</p>
    </div>
    <div className="history-filters">
      <label className="field"><span>Action</span>
        <select value={action} onChange={(event) => { setPage(1); setAction(event.target.value) }}>
          <option value="">All actions</option>
          {ACTIONS.map((name) => <option key={name} value={name}>{name}</option>)}
        </select>
      </label>
      <label className="field"><span>Entity</span>
        <input value={entity} onChange={(event) => setEntity(event.target.value)} placeholder="ApiKey, User, Organization…" />
      </label>
      <button className="button button-primary" onClick={() => { setPage(1); void load() }}>Apply</button>
    </div>
    {loading ? <div className="empty">Loading activity…</div>
      : error ? <div className="empty error" role="alert">{error}<br /><button className="retry-button" onClick={() => void load()}>Retry</button></div>
      : data && data.items.length ? <div className="table-wrap"><table>
          <thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Entity</th><th>Organization</th><th>Details</th></tr></thead>
          <tbody>
            {data.items.map((entry) => <tr key={entry.id}>
              <td>{new Date(entry.occurredAt).toLocaleString()}</td>
              <td>{entry.actorEmail ?? <span className="muted">{entry.actorType}</span>}</td>
              <td><span className="badge">{entry.action}</span></td>
              <td>{entry.entity}{entry.entityId && <><br /><code>{entry.entityId.slice(0, 12)}</code></>}</td>
              <td>{entry.organizationName ?? <span className="muted">—</span>}</td>
              <td>{entry.details ?? <span className="muted">—</span>}</td>
            </tr>)}
          </tbody>
        </table></div>
      : <div className="empty">No activity matches these filters.</div>}
    <div className="pager">
      <button disabled={page <= 1} onClick={() => setPage((p) => Math.max(1, p - 1))}>← Newer</button>
      <span>Page {data?.page ?? page}</span>
      <button disabled={!hasMore} onClick={() => setPage((p) => p + 1)}>Older →</button>
    </div>
  </section>
}
