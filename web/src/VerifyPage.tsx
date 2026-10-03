import { FormEvent, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, ApiError } from './api'
import { FormField, SubmitButton } from './AuthFlow'
import { VerificationHistoryPage } from './VerificationHistoryPage'
import type { Mode } from './mode'

type VerifyType = 'nin' | 'bvn' | 'phone'
type Identity = {
  fullName?: string | null; dateOfBirth?: string | null; gender?: string | null; phone?: string | null
  stateOfOrigin?: string | null; residentialAddress?: string | null; photo?: string | null
}
type Outcome = {
  id: string; type: VerifyType; environment: 'live' | 'sandbox'
  status: 'match' | 'no_match' | 'provider_error' | 'pending'
  message?: string | null; identity?: Identity | null
  charge: { amountKobo: number; refunded: boolean }; balanceAfterKobo?: number | null; createdAt: string
}
type TestNumbers = Record<VerifyType, { match: string; noMatch: string; providerError: string }>

const LABELS: Record<VerifyType, string> = { nin: 'NIN', bvn: 'BVN', phone: 'Phone number' }
const naira = (kobo: number) => `₦${(kobo / 100).toLocaleString('en-NG', { minimumFractionDigits: 2 })}`

// Mirrors IdentitySubject.Normalize on the server.
function validateNumber(type: VerifyType, raw: string): string | null {
  const value = raw.replace(/[\s\-()]/g, '')
  if (type === 'phone') {
    const local = value.startsWith('+234') ? '0' + value.slice(4) : value.startsWith('234') && value.length === 13 ? '0' + value.slice(3) : value
    return /^0[789][01]\d{8}$/.test(local) ? null : 'Enter a Nigerian mobile number, e.g. 08031234567.'
  }
  return /^\d{11}$/.test(value) ? null : `A ${LABELS[type]} must be exactly 11 digits.`
}

export function VerifyPage({ mode }: { mode: Mode }) {
  const test = mode === 'test'
  const [type, setType] = useState<VerifyType>('nin')
  const [number, setNumber] = useState('')
  const [fieldError, setFieldError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<Outcome | null>(null)
  const [error, setError] = useState<{ message: string; walletLink?: boolean } | null>(null)
  const [busy, setBusy] = useState(false)
  const [testNumbers, setTestNumbers] = useState<TestNumbers | null>(null)
  const [historyKey, setHistoryKey] = useState(0)
  const [prices, setPrices] = useState<Record<string, number | null>>({})

  useEffect(() => {
    // Only the sandbox serves test numbers; a 404 simply means "live".
    api.get<TestNumbers>('/v1/verify/test-numbers').then(setTestNumbers).catch(() => setTestNumbers(null))
    api.get<{ type: string; priceKobo: number | null }[]>('/v1/tenant/pricing')
      .then((rates) => setPrices(Object.fromEntries(rates.map((r) => [r.type, r.priceKobo]))))
      .catch(() => setPrices({}))
  }, [])

  async function submit(event: FormEvent) {
    event.preventDefault()
    const invalid = validateNumber(type, number)
    setFieldError(invalid)
    if (invalid) return
    setBusy(true); setError(null); setOutcome(null)
    try {
      setOutcome(await api.post<Outcome>(`/v1/verify/${type}`, { number, idempotencyKey: `web-${crypto.randomUUID()}` }))
    } catch (reason) {
      // 502 = the provider failed; the body is a full outcome (status provider_error, refunded) worth showing.
      const failed = reason instanceof ApiError && reason.status === 502 ? reason.body as Outcome | null : null
      if (failed?.status === 'provider_error') { setOutcome(failed); return }
      const status = reason instanceof ApiError ? reason.status : 0
      setError({ message: reason instanceof Error ? reason.message : 'Verification could not be completed.', walletLink: status === 402 })
    } finally {
      setBusy(false)
      setHistoryKey((k) => k + 1)
    }
  }

  return <section>
    <div className="page-title">
      <div className="eyebrow">VERIFICATION WORKSPACE {test && <span className="env-badge">TEST MODE</span>}</div>
      <h1>Run a verification.</h1>
      <p className="lede">{test
        ? 'Test mode: checks are free and only the test numbers below return data. Switch to Live (once approved) for real lookups.'
        : 'Check a NIN, BVN or phone number against the national registry. Each completed check is charged to your wallet; provider errors are refunded automatically.'}</p>
    </div>
    <div className="verify-layout">
      <div className="form-card">
        <form onSubmit={submit} noValidate>
          <div className="segmented" role="radiogroup" aria-label="Verification type">
            {(Object.keys(LABELS) as VerifyType[]).map((t) => (
              <button type="button" role="radio" aria-checked={type === t} key={t} className={type === t ? 'active' : ''}
                onClick={() => { setType(t); setNumber(''); setFieldError(null); setOutcome(null); setError(null) }}>
                {LABELS[t]}
              </button>
            ))}
          </div>
          <FormField label={LABELS[type]} inputMode="numeric" autoComplete="off" value={number} error={fieldError}
            placeholder={type === 'phone' ? '08031234567' : '11-digit number'}
            hint={type === 'phone' ? 'Local (080…) or international (+234…) format.' : undefined}
            onChange={(e) => { setNumber(e.target.value); if (fieldError) setFieldError(null) }} />
          <SubmitButton busy={busy} busyLabel="Checking the registry…">
            Verify {LABELS[type]}{test ? <span className="price-tag">FREE</span> : prices[type] != null && <span className="price-tag">{naira(prices[type]!)}</span>} ↗
          </SubmitButton>
        </form>
        {error && <div className="notice error" role="alert">
          {error.message} {error.walletLink && <Link className="text-link" to="/wallet">Fund wallet →</Link>}
        </div>}
        {test && testNumbers && <div className="sandbox-help">
          <div className="eyebrow">TEST NUMBERS</div>
          <p>No real lookups happen in test mode. Use these to see each outcome:</p>
          <dl>
            <dt>Match</dt><dd><button type="button" className="link-button" onClick={() => setNumber(testNumbers[type].match)}>{testNumbers[type].match}</button></dd>
            <dt>No match</dt><dd><button type="button" className="link-button" onClick={() => setNumber(testNumbers[type].noMatch)}>{testNumbers[type].noMatch}</button></dd>
            <dt>Provider error</dt><dd><button type="button" className="link-button" onClick={() => setNumber(testNumbers[type].providerError)}>{testNumbers[type].providerError}</button></dd>
          </dl>
        </div>}
      </div>

      {busy && <div className="result-card result-loading" aria-hidden="true"><div className="skeleton photo" /><div className="skeleton line" /><div className="skeleton line short" /></div>}
      {outcome && <ResultCard outcome={outcome} />}
    </div>
    <div className="verify-history-embed"><VerificationHistoryPage key={historyKey} /></div>
  </section>
}

function ResultCard({ outcome }: { outcome: Outcome }) {
  const verdict = {
    match: { label: 'MATCH FOUND', className: 'match', title: outcome.identity?.fullName ?? 'Identity confirmed' },
    no_match: { label: 'NO MATCH', className: 'pending', title: 'No record matches this number' },
    provider_error: { label: 'PROVIDER ERROR', className: 'failed', title: 'The check could not be completed' },
    pending: { label: 'PROCESSING', className: 'pending', title: 'Still processing' },
  }[outcome.status]
  const id = outcome.identity
  const rows: [string, string | null | undefined][] = id ? [
    ['DATE OF BIRTH', id.dateOfBirth], ['GENDER', id.gender], ['PHONE', id.phone],
    ['STATE OF ORIGIN', id.stateOfOrigin], ['ADDRESS', id.residentialAddress],
  ] : []

  return <div className={`result-card verdict-${verdict.className}`} role="status">
    <div className="result-head">
      <span className={`badge ${verdict.className}`}>{verdict.label}</span>
      {outcome.environment === 'sandbox' && <span className="env-badge">TEST</span>}
    </div>
    <div className="result-identity">
      {id?.photo && <img className="id-photo" src={id.photo} alt={`Registry photo of ${id.fullName ?? 'the subject'}`} />}
      <div>
        <h2>{verdict.title}</h2>
        {outcome.message && <p className="stat-note">{outcome.message}</p>}
      </div>
    </div>
    {rows.length > 0 && <div className="result-grid">
      {rows.filter(([, v]) => v).map(([k, v]) => <div key={k}><span>{k}</span><strong>{v}</strong></div>)}
    </div>}
    <div className="result-grid result-meta">
      <div><span>CHARGE</span><strong>{outcome.environment === 'sandbox' && outcome.charge.amountKobo === 0 ? 'Free (test mode)' : <>{naira(outcome.charge.amountKobo)}{outcome.charge.refunded && <span className="refund-note"> · refunded</span>}</>}</strong></div>
      <div><span>WALLET AFTER</span><strong>{outcome.balanceAfterKobo != null ? naira(outcome.balanceAfterKobo) : '—'}</strong></div>
      <div><span>REFERENCE</span><strong><code>{outcome.id.slice(0, 13)}</code></strong></div>
    </div>
    {id?.photo && <p className="stat-note">The photo and address are shown once and are not stored in your history.</p>}
  </div>
}
