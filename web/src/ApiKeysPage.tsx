import { FormEvent, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, AuthProfile } from './api'
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
  environment?: 'test' | 'live'
}

type Outlet = { id: string; name: string; status: string }

export function ApiKeysPage({ profile }: { profile: AuthProfile }) {
  // Agency admins issue keys per Outlet; everyone else issues organization keys.
  const agency = profile.tenantRole === 'agency_admin'
  const liveEnabled = Boolean(profile.liveEnabled)
  const base = agency ? '/v1/tenant/api-keys' : '/v1/api-keys'
  const [keys, setKeys] = useState<KeyRecord[]>([])
  const [outlets, setOutlets] = useState<Outlet[]>([])
  const [outletId, setOutletId] = useState('')
  const [description, setDescription] = useState('')
  const [environment, setEnvironment] = useState<'test' | 'live'>('test')
  const [rawKey, setRawKey] = useState('')
  const [notice, setNotice] = useState<{ text: string; error?: boolean } | null>(null)
  const [busy, setBusy] = useState(false)

  async function load() {
    try {
      setKeys(await api.get<KeyRecord[]>(base))
      if (agency) setOutlets(await api.get<Outlet[]>('/v1/tenant/outlets'))
    } catch (error) {
      setKeys([])
      setNotice({ text: error instanceof Error ? `Could not load API keys: ${error.message}` : 'Could not load API keys.', error: true })
    }
  }

  useEffect(() => { void load() }, [])

  async function create(event: FormEvent) {
    event.preventDefault()
    if (busy) return
    setNotice(null); setBusy(true)
    try {
      const result = await api.post<KeyRecord>(agency ? `/v1/tenant/api-keys/outlets/${outletId}` : '/v1/api-keys', { description, environment })
      setRawKey(result.rawKey ?? '')
      setOutletId('')
      setDescription('')
      setNotice({ text: `${environment === 'live' ? 'Live' : 'Test'} key created. Copy it now; it won't be shown again.` })
      await load()
    } catch (error) {
      setNotice({ text: error instanceof Error ? error.message : 'Could not create API key.', error: true })
    } finally { setBusy(false) }
  }

  async function revoke(id: string) {
    if (busy) return
    setBusy(true)
    try { await api.delete(`${base}/${id}`); await load() }
    catch (error) { setNotice({ text: error instanceof Error ? error.message : 'Could not revoke API key.', error: true }) }
    finally { setBusy(false) }
  }

  return <section>
    <div className="page-title">
      <div className="eyebrow">DEVELOPER ACCESS</div>
      <h1>{agency ? 'Outlet API keys.' : 'API keys.'}</h1>
      <p className="lede">{agency
        ? 'Give each outlet its own credential and keep verification activity isolated.'
        : 'Test keys (trv_test_…) are free and return sandbox data. Live keys (trv_live_…) run real, billed checks.'}</p>
    </div>
    <div className="form-card">
      <form onSubmit={create}>
        {agency && <label className="field"><span>Outlet</span><select required value={outletId} onChange={event => setOutletId(event.target.value)}><option value="">Select an outlet</option>{outlets.map(outlet => <option key={outlet.id} value={outlet.id}>{outlet.name}</option>)}</select></label>}
        <div className="field"><span>Environment</span>
          <div className="segmented two" role="radiogroup" aria-label="Key environment">
            <button type="button" role="radio" aria-checked={environment === 'test'} className={environment === 'test' ? 'active' : ''} onClick={() => setEnvironment('test')}>Test · free</button>
            <button type="button" role="radio" aria-checked={environment === 'live'} disabled={!liveEnabled} className={environment === 'live' ? 'active' : ''} onClick={() => setEnvironment('live')}
              title={liveEnabled ? 'Real, billed verifications' : 'Live keys unlock once TruvoID approves your organization profile'}>Live</button>
          </div>
          {!liveEnabled && <span className="field-hint">Live keys unlock once your organization profile is approved. <Link className="text-link" to="/setup">Organization setup →</Link></span>}
        </div>
        <label className="field"><span>Description</span><input required value={description} onChange={event => setDescription(event.target.value)} placeholder={environment === 'live' ? 'Production server' : 'Staging integration'} /></label>
        <button disabled={busy} className="button button-primary">{busy ? 'Working...' : `Generate ${environment} key ↗`}</button>
      </form>
      {rawKey && <div className="key-reveal"><span>Store this secret securely.</span><code>{rawKey}</code><CopyButton value={rawKey} label="Copy API key" /></div>}
      {notice && <div className={`notice ${notice.error ? 'error' : 'success'}`} role="status">{notice.text}</div>}
    </div>
    {keys.length ? <div className="table-wrap"><table><thead><tr><th>Key</th><th>Mode</th><th>Scope</th><th>Description</th><th>Status</th><th>Created</th><th /></tr></thead><tbody>{keys.map(key => <tr key={key.id}>
      <td><code>{key.keyPrefix}</code></td>
      <td>{key.environment === 'live' ? <span className="badge active">LIVE</span> : <span className="env-badge small">TEST</span>}</td>
      <td>{key.scope ?? 'organization'}</td><td>{key.description ?? '-'}</td><td>{key.status === 0 ? 'Active' : 'Revoked'}</td>
      <td>{new Date(key.createdAt).toLocaleDateString()}</td>
      <td>{key.status === 0 && <button disabled={busy} className="link-button" onClick={() => void revoke(key.id)}>Revoke</button>}</td></tr>)}</tbody></table></div>
      : <div className="empty">No API keys yet. Start with a test key — it's free.</div>}
    <div className="activity-card"><div><div className="eyebrow">INTEGRATION GUIDE</div><h2>Make your first call.</h2><p>Send <code>X-API-Key</code> to <code>POST /v1/verify/nin</code> with <code>{'{"number": "00000000001"}'}</code> using a test key.</p></div><a className="button button-primary" href="/api-docs" target="_blank" rel="noreferrer">Read API docs ↗</a></div>
  </section>
}
