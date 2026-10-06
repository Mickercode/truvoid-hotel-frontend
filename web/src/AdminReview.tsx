import { useEffect, useState } from 'react'
import { api } from './api'

type Json = Record<string, unknown>
type Setup = {
  sections: Record<string, Json>; accessLevel?: number; attested: boolean; status: string; progress: number
  reviewNote?: string | null; submittedAt?: string | null
  documents: { id: string; documentType: string; fileName: string; status: string }[]
}

const SECTION_TITLES: Record<string, string> = {
  general: 'Organization details', contacts: 'Contacts', business: 'Business', ownership: 'Ownership',
  directors: 'Directors', services: 'Verification services', integration: 'Integration', compliance: 'Compliance', legal: 'Legal history',
}

const label = (key: string) => key.replace(/([A-Z])/g, ' $1').replace(/^./, (c) => c.toUpperCase())
const display = (value: unknown) => (typeof value === 'boolean' ? (value ? 'Yes' : 'No') : String(value ?? ''))

/**
 * Platform-admin review of a submitted organization profile. Approving unlocks live
 * verification for the organization; requesting changes sends it back with a note.
 */
export function AdminReview({ organization, onClose, onDecided }: {
  organization: { id: string; name: string }
  onClose: () => void
  onDecided: () => void
}) {
  const [setup, setSetup] = useState<Setup | null>(null)
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState<'approve' | 'changes' | null>(null)
  const [message, setMessage] = useState<{ text: string; error?: boolean } | null>(null)

  useEffect(() => {
    api.get<Setup>(`/v1/admin/organizations/${organization.id}/setup`)
      .then(setSetup)
      .catch((e) => setMessage({ text: e instanceof Error ? e.message : 'Profile could not be loaded.', error: true }))
  }, [organization.id])

  async function decide(action: 'approve' | 'changes') {
    if (action === 'changes' && !note.trim()) {
      setMessage({ text: 'Write a note telling the organization what to change.', error: true })
      return
    }
    setBusy(action); setMessage(null)
    try {
      const result = await api.post<{ message: string }>(
        `/v1/admin/organizations/${organization.id}/setup/${action === 'approve' ? 'approve' : 'request-changes'}`,
        { note: note.trim() || null })
      setMessage({ text: result.message })
      onDecided()
    } catch (e) {
      setMessage({ text: e instanceof Error ? e.message : 'The decision could not be saved.', error: true })
    } finally {
      setBusy(null)
    }
  }

  async function openDocument(id: string) {
    // Open the tab synchronously (before the await) or pop-up blockers drop it.
    const tab = window.open('', '_blank')
    try {
      const url = await api.download(`/v1/admin/organizations/${organization.id}/documents/${id}`)
      if (tab) tab.location.href = url
      else setMessage({ text: 'Allow pop-ups to view documents, or your browser blocked the tab.', error: true })
    } catch (e) {
      tab?.close()
      setMessage({ text: e instanceof Error ? e.message : 'Document could not be opened.', error: true })
    }
  }

  const reviewable = setup?.status === 'submitted'
  return <div className="form-card review-panel">
    <div className="review-head">
      <div><div className="eyebrow">PROFILE REVIEW</div><h2>{organization.name}</h2></div>
      <button className="link-button" onClick={onClose}>Close ✕</button>
    </div>
    {!setup ? <div className="empty">{message?.text ?? 'Loading profile…'}</div> : <>
      <p className="stat-note">
        Status: <span className={`badge ${setup.status === 'approved' ? 'active' : setup.status === 'needs_changes' ? 'failed' : 'pending'}`}>{setup.status.replace('_', ' ')}</span>
        {' '}· {setup.progress}% complete · Access level {setup.accessLevel ?? '—'} · Attested: {setup.attested ? 'yes' : 'no'}
        {setup.submittedAt && <> · Submitted {new Date(setup.submittedAt).toLocaleString()}</>}
      </p>
      <div className="review-sections">
        {Object.entries(SECTION_TITLES).map(([key, title]) => {
          // Show every field the organization saved, including blank ones, so the
          // reviewer can see the profile as submitted rather than a false "Not provided".
          const entries = Object.entries(setup.sections[key] ?? {})
          return <div className="review-section" key={key}>
            <h3>{title}</h3>
            {entries.length ? <dl>{entries.map(([k, v]) => <div key={k}><dt>{label(k)}</dt><dd>{display(v) || <span className="muted">—</span>}</dd></div>)}</dl>
              : <p className="muted">Not provided</p>}
          </div>
        })}
      </div>
      <h3>Documents</h3>
      {setup.documents.length ? <div className="document-list">{setup.documents.map((d) => <div className="document-row" key={d.id}>
        <strong>{d.fileName}</strong><span>{d.documentType.replace(/_/g, ' ')}</span>
        <button className="link-button" onClick={() => void openDocument(d.id)}>Open ↗</button>
      </div>)}</div> : <div className="empty">No documents uploaded.</div>}
      {setup.status === 'approved' ? <p className="stat-note">This profile is approved. Live verification is enabled.</p> : <div className="review-decision">
        <label className="field"><span>Note to the organization (required to request changes)</span>
          <textarea rows={3} value={note} onChange={(e) => setNote(e.target.value)} placeholder="e.g. Upload a CAC certificate dated within the last 12 months." /></label>
        <div className="review-actions">
          <button className="button button-primary" disabled={busy !== null || !reviewable} onClick={() => void decide('approve')}>
            {busy === 'approve' ? <><span className="spinner" aria-hidden="true" />Approving…</> : 'Approve — enable live ↗'}</button>
          <button className="button button-secondary" disabled={busy !== null} onClick={() => void decide('changes')}>
            {busy === 'changes' ? 'Sending…' : 'Request changes'}</button>
        </div>
        {!reviewable && <p className="stat-note">Approval unlocks once the organization submits its profile. You can still send it back with a note.</p>}
      </div>}
    </>}
    {message && setup && <div className={`notice ${message.error ? 'error' : 'success'}`} role="status">{message.text}</div>}
  </div>
}
