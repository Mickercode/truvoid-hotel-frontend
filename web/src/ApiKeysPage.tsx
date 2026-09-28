import { FormEvent, useEffect, useState } from 'react'
import { api } from './api'

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

export function ApiKeysPage() {
  const [keys, setKeys] = useState<KeyRecord[]>([])
  const [outletId, setOutletId] = useState('')
  const [description, setDescription] = useState('')
  const [rawKey, setRawKey] = useState('')
  const [notice, setNotice] = useState('')

  async function load() {
    try { setKeys(await api.get<KeyRecord[]>('/v1/tenant/api-keys')) } catch { setKeys([]) }
  }

  useEffect(() => { void load() }, [])

  async function create(event: FormEvent) {
    event.preventDefault()
    setNotice('')
    try {
      const result = await api.post<KeyRecord>(`/v1/tenant/api-keys/outlets/${outletId}`, { description })
      setRawKey(result.rawKey ?? '')
      setOutletId('')
      setDescription('')
      setNotice('API key created. Copy it now; it will not be shown again.')
      await load()
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Could not create API key.')
    }
  }

  async function revoke(id: string) {
    await api.delete(`/v1/tenant/api-keys/${id}`)
    await load()
  }

  return <section>
    <div className="page-title">
      <div className="eyebrow">AGENCY / DEVELOPER ACCESS</div>
      <h1>Outlet API keys.</h1>
      <p className="lede">Give each outlet its own credential and keep verification activity isolated.</p>
    </div>
    <div className="form-card">
      <form onSubmit={create}>
        <label className="field"><span>Outlet ID</span><input required value={outletId} onChange={event => setOutletId(event.target.value)} placeholder="Outlet UUID" /></label>
        <label className="field"><span>Description</span><input required value={description} onChange={event => setDescription(event.target.value)} placeholder="Lagos outlet production" /></label>
        <button className="button button-primary">Generate outlet key ↗</button>
      </form>
      {rawKey && <div className="key-reveal"><span>Store this secret securely.</span><code>{rawKey}</code></div>}
      {notice && <div className="notice success">{notice}</div>}
    </div>
    {keys.length ? <div className="table-wrap"><table><thead><tr><th>Key</th><th>Scope</th><th>Description</th><th>Status</th><th>Created</th><th /></tr></thead><tbody>{keys.map(key => <tr key={key.id}><td><code>{key.keyPrefix}</code></td><td>{key.scope ?? 'outlet'}</td><td>{key.description ?? '-'}</td><td>{key.status === 0 ? 'Active' : 'Revoked'}</td><td>{new Date(key.createdAt).toLocaleDateString()}</td><td><button className="link-button" onClick={() => void revoke(key.id)}>Revoke</button></td></tr>)}</tbody></table></div> : <div className="empty">No outlet API keys have been created.</div>}
    <div className="activity-card"><div><div className="eyebrow">INTEGRATION GUIDE</div><h2>Connect an outlet.</h2><p>Use the X-API-Key header with the tenant verification reservation endpoint.</p></div><a className="button button-primary" href="/api-docs.html" target="_blank" rel="noreferrer">Read API docs ↗</a></div>
  </section>
}
