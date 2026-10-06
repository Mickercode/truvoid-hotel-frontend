import { useEffect, useState } from 'react'
import { api } from './api'

type Totals = { creditSalesKobo: number; refundsKobo: number; netKobo: number; entries: number }
type TypeRow = { verificationType: string; amountKobo: number; count: number }
type OrgRow = { organizationId: string; name: string; amountKobo: number }
type Recent = { occurredAt: string; organizationName: string; entryType: string; amountKobo: number; verificationType: string | null; reference: string | null }
type Financials = { rangeDays: number; totals: Totals; byType: TypeRow[]; topOrganizations: OrgRow[]; recent: Recent[] }

const naira = (kobo: number) => `₦${(kobo / 100).toLocaleString('en-NG', { minimumFractionDigits: 2 })}`

/** Platform revenue from the central ledger. */
export function AdminFinancialsPage() {
  const [days, setDays] = useState(30)
  const [data, setData] = useState<Financials | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  async function load() {
    setLoading(true)
    setError('')
    try {
      setData(await api.get<Financials>(`/v1/admin/financials?days=${days}`))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Financials could not be loaded.')
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => { void load() }, [days])

  return <section>
    <div className="page-title">
      <div className="eyebrow">ADMIN / FINANCIALS</div>
      <h1>Revenue.</h1>
      <p className="lede">Credit sales, refunds, and net revenue from the central ledger. Outlet resales are excluded (visibility only).</p>
    </div>
    <div className="segmented" style={{ maxWidth: 380, marginBottom: 6 }} role="radiogroup" aria-label="Period">
      {[7, 30, 90].map((d) => (
        <button key={d} type="button" role="radio" aria-checked={days === d} className={days === d ? 'active' : ''} onClick={() => setDays(d)}>
          Last {d} days
        </button>
      ))}
    </div>

    {loading ? <div className="empty">Loading financials…</div>
      : error ? <div className="empty error" role="alert">{error}<br /><button className="retry-button" onClick={() => void load()}>Retry</button></div>
      : data && <>
        <div className="stat-grid">
          <div className="stat-card highlight"><span className="stat-label">CREDIT SALES</span><strong>{naira(data.totals.creditSalesKobo)}</strong><span className="stat-note">{data.totals.entries} ledger entries</span></div>
          <div className="stat-card"><span className="stat-label">REFUNDS</span><strong>{naira(data.totals.refundsKobo)}</strong><span className="stat-note">provider errors, reversed</span></div>
          <div className="stat-card"><span className="stat-label">NET REVENUE</span><strong>{naira(data.totals.netKobo)}</strong><span className="stat-note">last {data.rangeDays} days</span></div>
        </div>

        <div className="section-heading"><div><div className="eyebrow">BY SERVICE</div><h2>Revenue by verification type</h2></div></div>
        {data.byType.length ? <div className="table-wrap"><table>
          <thead><tr><th>Type</th><th>Revenue</th><th>Checks</th></tr></thead>
          <tbody>{data.byType.map((row) => <tr key={row.verificationType}>
            <td>{row.verificationType.toUpperCase()}</td><td>{naira(row.amountKobo)}</td><td>{row.count.toLocaleString()}</td>
          </tr>)}</tbody>
        </table></div> : <div className="empty">No credit sales in this period.</div>}

        <div className="section-heading"><div><div className="eyebrow">TOP BUYERS</div><h2>Revenue by organization</h2></div></div>
        {data.topOrganizations.length ? <div className="table-wrap"><table>
          <thead><tr><th>Organization</th><th>Revenue</th></tr></thead>
          <tbody>{data.topOrganizations.map((row) => <tr key={row.organizationId}>
            <td>{row.name}</td><td>{naira(row.amountKobo)}</td>
          </tr>)}</tbody>
        </table></div> : <div className="empty">No credit sales in this period.</div>}

        <div className="section-heading"><div><div className="eyebrow">LEDGER</div><h2>Recent entries</h2></div></div>
        {data.recent.length ? <div className="table-wrap"><table>
          <thead><tr><th>Time</th><th>Organization</th><th>Type</th><th>Amount</th><th>Reference</th></tr></thead>
          <tbody>{data.recent.map((entry, index) => <tr key={index}>
            <td>{new Date(entry.occurredAt).toLocaleString()}</td>
            <td>{entry.organizationName}</td>
            <td><span className={`badge ${entry.entryType === 'credit_sale' ? 'active' : entry.entryType === 'refund' ? 'failed' : ''}`}>{entry.entryType.replace('_', ' ')}</span></td>
            <td>{naira(entry.amountKobo)}</td>
            <td>{entry.reference ? <code>{entry.reference.slice(0, 16)}</code> : <span className="muted">—</span>}</td>
          </tr>)}</tbody>
        </table></div> : <div className="empty">No ledger entries in this period.</div>}
      </>}
  </section>
}
