import { FormEvent, useState } from 'react'
import { api } from './api'
import { VerificationHistoryPage } from './VerificationHistoryPage'

type Result = Record<string, unknown>

export function VerifyPage() {
  const [type, setType] = useState('nin')
  const [subject, setSubject] = useState('')
  const [result, setResult] = useState<Result | null>(null)
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault(); setBusy(true); setMessage(''); setResult(null)
    try {
      setResult(await api.post<Result>('/v1/tenant/verification-calls/reserve', {
        verificationType: type,
        subjectRef: subject,
        idempotencyKey: `web-${crypto.randomUUID()}`,
      }))
      setMessage('Verification call reserved successfully.')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Verification could not be reserved.')
    } finally { setBusy(false) }
  }

  return <section>
    <div className="page-title"><div className="eyebrow">VERIFICATION WORKSPACE</div><h1>Run a verification.</h1><p className="lede">Reserve a verification call from your organization or outlet wallet with an auditable request.</p></div>
    <div className="verify-layout">
      <div className="form-card">
        <form onSubmit={submit}>
          <label className="field"><span>Verification type</span><select value={type} onChange={event => { setType(event.target.value); setSubject('') }}><option value="nin">NIN</option><option value="bvn">BVN</option><option value="phone">Phone number</option></select></label>
          <label className="field"><span>{type === 'nin' ? 'NIN' : type === 'bvn' ? 'BVN' : 'Phone number'}</span><input required value={subject} onChange={event => setSubject(event.target.value)} placeholder={type === 'phone' ? '080...' : `Enter ${type.toUpperCase()}`} /></label>
          <p className="stat-note">Use a hashed subject reference for production integrations. The reservation is charged against the active tenant scope.</p>
          <button className="button button-primary" disabled={busy}>{busy ? 'Reserving...' : 'Reserve verification ↗'}</button>
        </form>
        {message && <div className={message.includes('successfully') ? 'notice success' : 'notice error'}>{message}</div>}
      </div>
      {result && <div className="result-card"><div className="eyebrow">RESERVATION CONFIRMED</div><h2>{String(result.reference ?? result.verificationCallId ?? result.id ?? 'Verification reserved')}</h2><div className="result-grid"><div><span>TYPE</span><strong>{String(result.verificationType ?? type).toUpperCase()}</strong></div><div><span>WALLET AFTER</span><strong>{result.balanceAfterKobo ? `₦${Number(result.balanceAfterKobo) / 100}` : 'Recorded'}</strong></div><div><span>STATUS</span><strong>{String(result.status ?? 'reserved')}</strong></div></div><p className="stat-note">Keep the reservation reference when reconciling the verification result.</p></div>}
    </div><div className="verify-history-embed"><VerificationHistoryPage /></div>
  </section>
}
