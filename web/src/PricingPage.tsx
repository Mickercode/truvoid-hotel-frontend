import { FormEvent, useEffect, useState } from 'react'
import { api } from './api'

type Rate = { type: string; name: string; priceKobo: number | null; costKobo: number | null; effectiveFrom: string | null }
type Draft = { price: string; cost: string }

const toNaira = (kobo: number | null) => (kobo == null ? '' : (kobo / 100).toFixed(2))
const toKobo = (naira: string) => Math.round(Number(naira) * 100)
const money = (kobo: number | null) => (kobo == null ? '—' : `₦${(kobo / 100).toLocaleString('en-NG', { minimumFractionDigits: 2 })}`)

/**
 * Platform-wide price per verification type. Saving appends a new version effective
 * immediately; past charges keep the price they were made at. Per-organization
 * overrides go through PUT /v1/admin/pricing/organizations/{id}/{type}.
 */
export function PricingPage() {
  const [rates, setRates] = useState<Rate[] | null>(null)
  const [drafts, setDrafts] = useState<Record<string, Draft>>({})
  const [saving, setSaving] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ type: string; text: string; error?: boolean } | null>(null)
  const [loadError, setLoadError] = useState('')

  async function load() {
    setLoadError('')
    try {
      const result = await api.get<Rate[]>('/v1/admin/pricing')
      setRates(result)
      setDrafts(Object.fromEntries(result.map((r) => [r.type, { price: toNaira(r.priceKobo), cost: toNaira(r.costKobo) }])))
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : 'Pricing could not be loaded.')
    }
  }
  useEffect(() => { void load() }, [])

  async function save(event: FormEvent, rate: Rate) {
    event.preventDefault()
    const draft = drafts[rate.type]
    const price = toKobo(draft.price), cost = toKobo(draft.cost)
    if (!draft.price || !draft.cost || Number.isNaN(price) || Number.isNaN(cost) || price < 0 || cost < 0) {
      setNotice({ type: rate.type, text: 'Enter a price and an upstream cost of ₦0 or more.', error: true })
      return
    }
    setSaving(rate.type); setNotice(null)
    try {
      const result = await api.put<{ warning?: string | null }>(`/v1/admin/pricing/${rate.type}`, { priceKobo: price, costKobo: cost })
      setNotice({ type: rate.type, text: result.warning ?? 'Saved. New verifications use this price immediately.', error: !!result.warning })
      await load()
    } catch (error) {
      setNotice({ type: rate.type, text: error instanceof Error ? error.message : 'Price could not be saved.', error: true })
    } finally {
      setSaving(null)
    }
  }

  return <section>
    <div className="page-title">
      <div className="eyebrow">ADMIN / PRICING</div>
      <h1>Verification pricing.</h1>
      <p className="lede">The default price every organization pays per check. Changes apply to new verifications immediately; past charges are unaffected.</p>
    </div>
    {loadError ? <div className="empty error" role="alert">{loadError}<br /><button className="retry-button" onClick={() => void load()}>Retry</button></div>
      : !rates ? <div className="empty">Loading pricing…</div>
      : <div className="pricing-grid">
        {rates.map((rate) => {
          const draft = drafts[rate.type] ?? { price: '', cost: '' }
          const margin = toKobo(draft.price) - toKobo(draft.cost)
          return <form className="form-card pricing-card" key={rate.type} onSubmit={(e) => void save(e, rate)} noValidate>
            <div className="pricing-head">
              <div><div className="eyebrow">{rate.type.toUpperCase()}</div><h2>{rate.name}</h2></div>
              {rate.priceKobo == null && <span className="badge failed">NOT SET</span>}
            </div>
            <p className="stat-note">Current: {money(rate.priceKobo)} per check{rate.effectiveFrom && ` · since ${new Date(rate.effectiveFrom).toLocaleDateString()}`}</p>
            <div className="pricing-fields">
              <label className="field"><span>Customer price (₦)</span>
                <input inputMode="decimal" value={draft.price} onChange={(e) => setDrafts({ ...drafts, [rate.type]: { ...draft, price: e.target.value } })} /></label>
              <label className="field"><span>Upstream cost (₦)</span>
                <input inputMode="decimal" value={draft.cost} onChange={(e) => setDrafts({ ...drafts, [rate.type]: { ...draft, cost: e.target.value } })} /></label>
            </div>
            {draft.price && draft.cost && !Number.isNaN(margin) &&
              <p className={`margin ${margin < 0 ? 'negative' : ''}`}>Margin: {money(margin)} per check</p>}
            {notice?.type === rate.type && <div className={`notice ${notice.error ? 'error' : 'success'}`} role="status">{notice.text}</div>}
            <button className="button button-primary auth-submit" disabled={saving === rate.type}>
              {saving === rate.type ? <><span className="spinner" aria-hidden="true" />Saving…</> : 'Save price'}
            </button>
          </form>
        })}
      </div>}
  </section>
}
