import { FormEvent, useState } from 'react'
import { api, AuthProfile, tokenStore } from './api'
import { validatePassword } from './validation'

type ChangePasswordResponse = { message?: string; accessToken: string; refreshToken: string; expiresAt: string }

/**
 * Account settings: change password (endpoint rotates the session) and, for an
 * organization administrator, deactivate the whole organization.
 */
export function SettingsPage({ profile, onLogout }: { profile: AuthProfile; onLogout: () => void }) {
  const isOrgAdmin = !profile.outletId && (profile.tenantRole === 'institution_admin' || profile.tenantRole === 'agency_admin')

  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [pwMessage, setPwMessage] = useState<{ text: string; error?: boolean } | null>(null)
  const [pwBusy, setPwBusy] = useState(false)

  const [confirming, setConfirming] = useState(false)
  const [deactPassword, setDeactPassword] = useState('')
  const [deactMessage, setDeactMessage] = useState<{ text: string; error?: boolean } | null>(null)
  const [deactBusy, setDeactBusy] = useState(false)

  async function changePassword(event: FormEvent) {
    event.preventDefault()
    if (pwBusy) return
    const invalid = validatePassword(next)
    if (invalid) { setPwMessage({ text: invalid, error: true }); return }
    if (next !== confirm) { setPwMessage({ text: "Passwords don't match.", error: true }); return }
    if (!current) { setPwMessage({ text: 'Enter your current password.', error: true }); return }

    setPwBusy(true)
    setPwMessage(null)
    try {
      const result = await api.post<ChangePasswordResponse>('/v1/auth/change-password', {
        currentPassword: current,
        newPassword: next,
      })
      // The endpoint revokes other devices and issues a fresh session for this one.
      tokenStore.save(result)
      setCurrent(''); setNext(''); setConfirm('')
      setPwMessage({ text: result.message ?? 'Password changed. Other devices were signed out.' })
    } catch (reason) {
      setPwMessage({ text: reason instanceof Error ? reason.message : 'Password could not be changed.', error: true })
    } finally {
      setPwBusy(false)
    }
  }

  async function deactivate(event: FormEvent) {
    event.preventDefault()
    if (deactBusy) return
    setDeactBusy(true)
    setDeactMessage(null)
    try {
      await api.post('/v1/auth/deactivate', { password: deactPassword })
      tokenStore.clear()
      onLogout()
    } catch (reason) {
      setDeactMessage({ text: reason instanceof Error ? reason.message : 'The organization could not be deactivated.', error: true })
      setDeactBusy(false)
    }
  }

  return <section>
    <div className="page-title">
      <div className="eyebrow">ACCOUNT / SETTINGS</div>
      <h1>Your account.</h1>
      <p className="lede">Manage your sign-in credentials{isOrgAdmin ? ' and your organization access' : ''}.</p>
    </div>

    <div className="section-heading"><div><div className="eyebrow">SECURITY</div><h2>Change password</h2></div></div>
    <div className="form-card narrow">
      <form onSubmit={changePassword} noValidate>
        <label className="field"><span>Current password</span>
          <input type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} /></label>
        <label className="field"><span>New password</span>
          <input type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} /></label>
        <label className="field"><span>Confirm new password</span>
          <input type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} /></label>
        <span className="field-hint">At least 8 characters, with a letter and a number.</span>
        <button className="button button-primary" disabled={pwBusy}>{pwBusy ? 'Saving…' : 'Change password'}</button>
      </form>
      {pwMessage && <div className={`notice ${pwMessage.error ? 'error' : 'success'}`} role="status">{pwMessage.text}</div>}
    </div>

    {isOrgAdmin && <>
      <div className="section-heading"><div><div className="eyebrow">DANGER ZONE</div><h2>Deactivate organization</h2></div></div>
      <p className="lede">
        Deactivating suspends the whole organization, disables its members, and signs everyone out.
        It cannot be undone from here — contact TruvoID support to reactivate.
      </p>
      <div className="form-card narrow">
        {!confirming ? (
          <button className="button button-secondary" onClick={() => setConfirming(true)}>Deactivate organization…</button>
        ) : (
          <form onSubmit={deactivate} noValidate>
            <label className="field"><span>Confirm with your current password</span>
              <input type="password" autoComplete="current-password" value={deactPassword} onChange={(e) => setDeactPassword(e.target.value)} /></label>
            <div className="setup-actions">
              <button className="button button-secondary" disabled={deactBusy}>{deactBusy ? 'Deactivating…' : 'Permanently deactivate'}</button>
              <button type="button" className="link-button" onClick={() => { setConfirming(false); setDeactPassword('') }}>Cancel</button>
            </div>
          </form>
        )}
        {deactMessage && <div className={`notice ${deactMessage.error ? 'error' : 'success'}`} role="alert">{deactMessage.text}</div>}
      </div>
    </>}
  </section>
}
