import { FormEvent, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import { CopyButton } from './CopyButton'
import { ConfirmButton } from './ConfirmButton'

type AdminKey = {
  id: string
  organizationId: string
  outletId: string | null
  keyPrefix: string
  description: string | null
  scope: string
  status: string
  callCount: number
  createdAt: string
  lastUsedAt: string | null
}
type Org = { id: string; name: string; type: string; status: string }
type CreatedKey = { id: string; keyPrefix: string; rawKey?: string | null; environment?: string }

const environmentOf = (key: AdminKey) => (key.keyPrefix.startsWith('trv_test_') ? 'test' : 'live')

/**
 * Every API key across all tenants, for platform staff. Keys are read-only here except
 * revocation; a key can also be minted on behalf of a tenant that cannot self-serve.
 */
export function ApiKeysAdminPage() {
  const [keys, setKeys] = useState<AdminKey[] | null>(null)
  const [organizations, setOrganizations] = useState<Org[]>([])
  const [loadError, setLoadError] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [actionError, setActionError] = useState('')
  const [filter, setFilter] = useState('')

  const [form, setForm] = useState({ organizationId: '', description: '', environment: 'test' })
  const [creating, setCreating] = useState(false)
  const [created, setCreated] = useState<CreatedKey | null>(null)
  const [createError, setCreateError] = useState('')

  const orgName = useMemo(() => new Map(organizations.map((o) => [o.id, o.name])), [organizations])

  async function load() {
    setLoadError('')
    try {
      setKeys(await api.get<AdminKey[]>('/v1/admin/api-keys'))
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : 'API keys could not be loaded.')
    }
  }
  useEffect(() => {
    void load()
    api.get<Org[]>('/v1/admin/organizations').then(setOrganizations).catch(() => undefined)
  }, [])

  async function revoke(id: string) {
    if (busy) return
    setBusy(id)
    setActionError('')
    try {
      await api.post(`/v1/admin/api-keys/${id}/revoke`, {})
      await load()
    } catch (error) {
      setActionError(error instanceof Error ? error.message : 'The key could not be revoked.')
    } finally {
      setBusy(null)
    }
  }

  async function create(event: FormEvent) {
    event.preventDefault()
    if (!form.organizationId) {
      setCreateError('Choose an organization.')
      return
    }
    setCreating(true)
    setCreated(null)
    setCreateError('')
    try {
      const result = await api.post<CreatedKey>('/v1/admin/api-keys/tenants', {
        organizationId: form.organizationId,
        description: form.description || null,
        environment: form.environment,
      })
      setCreated(result)
      setForm({ organizationId: form.organizationId, description: '', environment: form.environment })
      await load()
    } catch (error) {
      setCreateError(error instanceof Error ? error.message : 'The key could not be created.')
    } finally {
      setCreating(false)
    }
  }

  const visible = (keys ?? []).filter((key) => {
    if (!filter.trim()) return true
    const needle = filter.trim().toLowerCase()
    return (orgName.get(key.organizationId) ?? '').toLowerCase().includes(needle)
      || key.keyPrefix.toLowerCase().includes(needle)
      || (key.description ?? '').toLowerCase().includes(needle)
  })

  return <section>
    <div className="page-title">
      <div className="eyebrow">ADMIN / API KEYS</div>
      <h1>API keys.</h1>
      <p className="lede">Every key issued across the platform, with its tenant, environment, and usage. Revoking a key takes effect immediately.</p>
    </div>

    <div className="form-card narrow">
      <div className="eyebrow">ISSUE A TENANT KEY</div>
      <form onSubmit={create} noValidate>
        <label className="field"><span>Organization</span>
          <select required value={form.organizationId} onChange={(e) => setForm({ ...form, organizationId: e.target.value })}>
            <option value="">Select an organization…</option>
            {organizations.map((org) => <option key={org.id} value={org.id}>{org.name} ({org.type})</option>)}
          </select>
        </label>
        <label className="field"><span>Environment</span>
          <select value={form.environment} onChange={(e) => setForm({ ...form, environment: e.target.value })}>
            <option value="test">Test — free, sandbox data</option>
            <option value="live">Live — real, billed checks</option>
          </select>
        </label>
        <label className="field"><span>Description</span>
          <input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} placeholder="e.g. production server" />
        </label>
        <button className="button button-primary" disabled={creating}>{creating ? 'Creating…' : 'Generate key ↗'}</button>
      </form>
      {createError && <div className="notice error" role="alert">{createError}</div>}
      {created?.rawKey && (
        <div className="key-reveal">
          <span>Copy this key now — it won't be shown again</span>
          <code>{created.rawKey}</code>
          <CopyButton value={created.rawKey} label="Copy key" />
        </div>
      )}
    </div>

    <div className="section-heading">
      <div><div className="eyebrow">ALL KEYS</div><h2>Issued keys</h2></div>
      <label className="field"><span>Filter</span>
        <input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Organization, prefix, description" />
      </label>
    </div>
    {actionError && <div className="notice error" role="alert">{actionError}</div>}
    {loadError ? (
      <div className="empty error" role="alert">{loadError}<br /><button className="retry-button" onClick={() => void load()}>Retry</button></div>
    ) : !keys ? (
      <div className="empty">Loading API keys…</div>
    ) : visible.length ? (
      <div className="table-wrap">
        <table>
          <thead>
            <tr><th>Organization</th><th>Environment</th><th>Scope</th><th>Description</th><th>Calls</th><th>Status</th><th>Last used</th><th /></tr>
          </thead>
          <tbody>
            {visible.map((key) => (
              <tr key={key.id}>
                <td>{orgName.get(key.organizationId) ?? key.organizationId.slice(0, 8)}</td>
                <td><span className={environmentOf(key) === 'live' ? 'badge active' : 'env-badge small'}>{environmentOf(key).toUpperCase()}</span></td>
                <td>{key.scope}</td>
                <td>{key.description ?? <span className="muted">—</span>}</td>
                <td>{key.callCount.toLocaleString()}</td>
                <td><span className={`badge ${key.status === 'active' ? 'active' : 'failed'}`}>{key.status}</span></td>
                <td>{key.lastUsedAt ? new Date(key.lastUsedAt).toLocaleString() : <span className="muted">never</span>}</td>
                <td>
                  {key.status === 'active' && (
                    <ConfirmButton
                      label="Revoke"
                      question="Revoke this key?"
                      confirmLabel="Revoke"
                      disabled={busy === key.id}
                      onConfirm={() => void revoke(key.id)}
                    />
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    ) : (
      <div className="empty">No API keys match.</div>
    )}
  </section>
}
