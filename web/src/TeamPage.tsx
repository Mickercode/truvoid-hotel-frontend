import { FormEvent, useEffect, useState } from 'react'
import { api, AuthProfile } from './api'
import { CopyButton } from './CopyButton'

type Member = { id: string; email: string; fullName?: string; role: string; status: string; outletId?: string; lastLoginAt?: string }
type Outlet = { id: string; name: string }

function Notice({ message, error = false }: { message: string; error?: boolean }) {
  return message ? <div className={`notice ${error ? 'error' : 'success'}`} role={error ? 'alert' : 'status'}>{message}</div> : null
}

export function TeamPage({ profile }: { profile: AuthProfile }) {
  const agency = profile.role.toLowerCase().includes('agency')
  const [members, setMembers] = useState<Member[]>([])
  const [outlets, setOutlets] = useState<Outlet[]>([])
  const [form, setForm] = useState({ fullName: '', email: '', role: agency ? 'agency_user' : 'institution_staff', outletId: '' })
  const [invite, setInvite] = useState('')
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)

  async function load() {
    try {
      setMembers(await api.get<Member[]>('/v1/tenant/team'))
      if (agency) setOutlets(await api.get<Outlet[]>('/v1/tenant/outlets'))
    } catch (error) {
      setMembers([])
      setMessage(error instanceof Error ? `Could not load team: ${error.message}` : 'Could not load team members.')
    }
  }

  useEffect(() => { void load() }, [])

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (busy) return
    setBusy(true)
    setMessage('Sending invitation...')
    try {
      const result = await api.post<{ invitationToken: string }>('/v1/tenant/team/invite', { ...form, outletId: form.outletId || null })
      const url = `${window.location.origin}/accept-team-invite?token=${result.invitationToken}`
      setInvite(url)
      setMessage('Invitation created. The team member will receive an email, or you can copy the link below.')
      setForm({ ...form, fullName: '', email: '', outletId: '' })
      await load()
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Could not invite team member.')
    } finally {
      setBusy(false)
    }
  }

  async function change(id: string, action: string) {
    if (busy) return
    setBusy(true)
    try {
      await api.post(`/v1/tenant/team/${id}/${action}`, {})
      await load()
      setMessage('Team member updated.')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Could not update team member.')
    } finally {
      setBusy(false)
    }
  }

  return <section>
    <div className="page-title"><div className="eyebrow">WORKSPACE / TEAM</div><h1>Your team.</h1><p className="lede">Invite people into the workspace without sharing administrator credentials.</p></div>
    <div className="form-card narrow">
      <div className="eyebrow">INVITE TEAM MEMBER</div>
      <form onSubmit={submit}>
        <label className="field"><span>Full name</span><input disabled={busy} required value={form.fullName} onChange={event => setForm({ ...form, fullName: event.target.value })} /></label>
        <label className="field"><span>Email</span><input disabled={busy} required type="email" value={form.email} onChange={event => setForm({ ...form, email: event.target.value })} /><small>They will receive a secure activation link from your administrator.</small></label>
        {agency && <label className="field"><span>Access scope</span><select disabled={busy} value={form.role} onChange={event => setForm({ ...form, role: event.target.value })}><option value="agency_user">Agency workspace</option><option value="outlet_staff">Specific outlet</option></select></label>}
        {agency && form.role === 'outlet_staff' && <label className="field"><span>Outlet</span><select disabled={busy} required value={form.outletId} onChange={event => setForm({ ...form, outletId: event.target.value })}><option value="">Select an outlet</option>{outlets.map(outlet => <option key={outlet.id} value={outlet.id}>{outlet.name}</option>)}</select></label>}
        <button disabled={busy} className="button button-primary">{busy ? 'Working...' : 'Generate invitation ↗'}</button>
      </form>
      <Notice message={message} error={message.startsWith('Could')} />
      {invite && <div className="key-reveal"><span>Invitation link</span><code>{invite}</code><CopyButton value={invite} label="Copy invitation link" /></div>}
    </div>
    {members.length ? <div className="table-wrap"><table><thead><tr><th>Member</th><th>Role</th><th>Outlet</th><th>Status</th><th>Last login</th><th /></tr></thead><tbody>{members.map(member => <tr key={member.id}><td><strong>{member.fullName || member.email}</strong><br /><span className="stat-note">{member.email}</span></td><td>{member.role}</td><td>{member.outletId ? String(member.outletId).slice(0, 8) : 'Agency'}</td><td><span className={`badge ${member.status}`}>{member.status}</span></td><td>{member.lastLoginAt ? new Date(member.lastLoginAt).toLocaleDateString() : 'Never'}</td><td><button disabled={busy} className="link-button" onClick={() => void change(member.id, member.status === 'active' ? 'disable' : 'reactivate')}>{member.status === 'active' ? 'Disable' : 'Reactivate'}</button></td></tr>)}</tbody></table></div> : <div className="empty">No team members yet.</div>}
  </section>
}
