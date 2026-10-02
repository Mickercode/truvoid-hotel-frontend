import { FormEvent, useEffect, useState } from 'react'
import { api } from './api'
import { AuthProfile } from './api'
import { CopyButton } from './CopyButton'

type KeyRecord = {
  id: string
  keyPrefix: string
  description?: string
  status: number
  scope?: string
  outletId?: string
  createdAt: string
  rawKey?: string
}

type Outlet = { id: string; name: string; status: string }

export function ApiKeysPage({ profile }: { profile: AuthProfile }) {
  const [keys, setKeys] = useState<KeyRecord[]>([])
  const [outlets, setOutlets] = useState<Outlet[]>([])
  const [outletId, setOutletId] = useState('')
  const [description, setDescription] = useState('')
  const [rawKey, setRawKey] = useState('')
  const [notice, setNotice] = useState(''); const [busy, setBusy] = useState(false)

  async function load() {
    try {
      setKeys(await api.get<KeyRecord[]>(profile.role.toLowerCase().includes('agency') ? '/v1/tenant/api-keys' : '/v1/api-keys'))
      if (profile.role.toLowerCase().includes('agency')) setOutlets(await api.get<Outlet[]>('/v1/tenant/outlets'))
      } catch (error) { setKeys([]); setNotice(error instanceof Error ? `Could not load API keys: ${error.message}` : 'Could not load API keys.') }
  }

  useEffect(() => { void load() }, [])

  async function create(event: FormEvent) {
    event.preventDefault()
    if (busy) return
    setNotice(''); setBusy(true)
    try {
      const agency = profile.role.toLowerCase().includes('agency')
      const result = await api.post<KeyRecord>(agency ? `/v1/tenant/api-keys/outlets/${outletId}` : '/v1/api-keys', { description })
      setRawKey(result.rawKey ?? '')
      setOutletId('')
      setDescription('')
      setNotice('API key created. Copy it now; it will not be shown again.')
      await load()
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Could not create API key.')
    } finally { setBusy(false) }
  }

  async function revoke(id: string) {
    if (busy) return
    setBusy(true)
    try { await api.delete(`${profile.role.toLowerCase().includes('agency') ? '/v1/tenant/api-keys' : '/v1/api-keys'}/${id}`); await load() } catch (error) { setNotice(error instanceof Error ? error.message : 'Could not revoke API key.') } finally { setBusy(false) }
  }

  return <section>
    <div className="page-title">
      <div className="eyebrow">AGENCY / DEVELOPER ACCESS</div>
      <h1>Outlet API keys.</h1>
      <p className="lede">{profile.role.toLowerCase().includes('agency') ? 'Give each outlet its own credential and keep verification activity isolated.' : 'Create credentials for your organization when your integration is ready.'}</p>
    </div>
    <div className="form-card">
      <form onSubmit={create}>
        {profile.role.toLowerCase().includes('agency') && <label className="field"><span>Outlet</span><select required value={outletId} onChange={event => setOutletId(event.target.value)}><option value="">Select an outlet</option>{outlets.map(outlet => <option key={outlet.id} value={outlet.id}>{outlet.name}</option>)}</select></label>}
        <label className="field"><span>Description</span><input required value={description} onChange={event => setDescription(event.target.value)} placeholder="Lagos outlet production" /></label>
        <button disabled={busy} className="button button-primary">{busy ? 'Working...' : 'Generate outlet key ↗'}</button>
      </form>
      {rawKey && <div className="key-reveal"><span>Store this secret securely.</span><code>{rawKey}</code><CopyButton value={rawKey} label="Copy API key" /></div>}
      {notice && <div className={`notice ${notice.startsWith('Could') || notice.includes('could not') ? 'error' : 'success'}`} role="status">{notice}</div>}
    </div>
    {keys.length ? <div className="table-wrap"><table><thead><tr><th>Key</th><th>Scope</th><th>Description</th><th>Status</th><th>Created</th><th /></tr></thead><tbody>{keys.map(key => <tr key={key.id}><td><code>{key.keyPrefix}</code></td><td>{key.scope ?? 'outlet'}</td><td>{key.description ?? '-'}</td><td>{key.status === 0 ? 'Active' : 'Revoked'}</td><td>{new Date(key.createdAt).toLocaleDateString()}</td><td><button disabled={busy} className="link-button" onClick={() => void revoke(key.id)}>Revoke</button></td></tr>)}</tbody></table></div> : <div className="empty">No outlet API keys have been created.</div>}
    <div className="activity-card"><div><div className="eyebrow">INTEGRATION GUIDE</div><h2>Connect an outlet.</h2><p>Use the X-API-Key header with the tenant verification reservation endpoint.</p></div><a className="button button-primary" href="/api-docs.html" target="_blank" rel="noreferrer">Read API docs ↗</a></div>
  </section>
}
