import { FormEvent, ReactNode, useEffect, useState } from 'react'
import { flushSync } from 'react-dom'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { api, ApiError, AuthProfile, tokenStore } from './api'
import { FormField, PasswordField, ProgressSteps, SubmitButton, useSteps, waitForWorkspace } from './AuthFlow'
import { validateEmail, validateName, validatePassword } from './validation'

type Frame = (props: { children: ReactNode; title: ReactNode; eyebrow?: string }) => ReactNode
const pause = (ms: number) => new Promise((r) => setTimeout(r, ms))
const messageOf = (error: unknown, fallback: string) => (error instanceof Error ? error.message : fallback)

function FormNotice({ message }: { message: string }) {
  return message ? <div className="notice error" role="alert">{message}</div> : null
}

// ── Sign in ──────────────────────────────────────────────────────────────────

export function Login({ onLogin, Frame }: { onLogin: (profile: AuthProfile) => void; Frame: Frame }) {
  const navigate = useNavigate()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [errors, setErrors] = useState<{ email?: string | null; password?: string | null }>({})
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const flow = useSteps(['Verifying credentials', 'Securing your session', 'Loading your workspace'])

  async function submit(event: FormEvent) {
    event.preventDefault()
    const found = { email: validateEmail(email), password: password ? null : 'Enter your password.' }
    setErrors(found)
    setMessage('')
    if (found.email || found.password) return

    flow.reset()
    setBusy(true)
    try {
      tokenStore.clear()
      tokenStore.save(await flow.run(0, () => api.login(email.trim(), password), 600))
      const profile = await flow.run(1, () => api.profile())
      const ready = await flow.run(2, async () =>
        (await waitForWorkspace(profile, (s) => flow.update(2, { detail: `Finishing your workspace setup… ${s}s` }))).profile)
      await pause(250)
      // Commit the signed-in tree first; navigating while the public routes are
      // still mounted would hit their catch-all redirect instead.
      flushSync(() => onLogin(ready))
      navigate('/dashboard', { replace: true })
    } catch (error) {
      tokenStore.clear()
      await pause(700) // let the failed step register before returning to the form
      setPassword('')
      setMessage(messageOf(error, 'Sign in failed. Please try again.'))
      setBusy(false)
    }
  }

  return (
    <Frame title={<>Verify with<br /><span className="gradient-text">confidence.</span></>}>
      {busy ? (
        <ProgressSteps title="SIGNING YOU IN" steps={flow.steps} />
      ) : (
        <>
          <form onSubmit={submit} noValidate>
            <FormField label="Email address" type="email" autoComplete="email" autoFocus={!message}
              value={email} error={errors.email}
              onChange={(e) => { setEmail(e.target.value); if (errors.email) setErrors({ ...errors, email: null }) }} />
            <PasswordField label="Password" autoComplete="current-password" autoFocus={!!message}
              value={password} error={errors.password}
              onChange={(e) => { setPassword(e.target.value); if (errors.password) setErrors({ ...errors, password: null }) }} />
            <FormNotice message={message} />
            <SubmitButton>Sign in ↗</SubmitButton>
          </form>
          <div className="auth-foot">
            <Link to="/register">Create an institution account</Link>
            <span> · </span>
            <Link to="/accept-agency-invite">Accept agency invitation</Link>
          </div>
        </>
      )}
    </Frame>
  )
}

// ── Institution sign-up ──────────────────────────────────────────────────────

type RegisterForm = { institutionName: string; adminFullName: string; adminEmail: string; password: string; confirmPassword: string }
type RegisterErrors = Partial<Record<keyof RegisterForm, string | null>>

function validateRegistration(form: RegisterForm): RegisterErrors {
  return {
    institutionName: validateName(form.institutionName, 'institution name'),
    adminFullName: validateName(form.adminFullName, "administrator's full name"),
    adminEmail: validateEmail(form.adminEmail),
    password: validatePassword(form.password),
    confirmPassword: form.confirmPassword === form.password ? null : "Passwords don't match.",
  }
}

// Server messages that belong to a specific field rather than the whole form.
function fieldForServerError(message: string): keyof RegisterForm | null {
  const text = message.toLowerCase()
  if (text.includes('password')) return 'password'
  if (text.includes('email')) return 'adminEmail'
  if (text.includes('institution name')) return 'institutionName'
  if (text.includes('full name')) return 'adminFullName'
  return null
}

export function Register({ onLogin, Frame }: { onLogin: (profile: AuthProfile) => void; Frame: Frame }) {
  const navigate = useNavigate()
  const [form, setForm] = useState<RegisterForm>({ institutionName: '', adminFullName: '', adminEmail: '', password: '', confirmPassword: '' })
  const [errors, setErrors] = useState<RegisterErrors>({})
  const [attempted, setAttempted] = useState(false)
  const [message, setMessage] = useState('')
  const [phase, setPhase] = useState<'form' | 'progress' | 'slow' | 'created'>('form')
  const [profile, setProfile] = useState<AuthProfile | null>(null)
  const flow = useSteps([
    'Creating your institution account',
    'Signing you in',
    'Provisioning your secure workspace',
  ])

  function set<K extends keyof RegisterForm>(key: K, value: string) {
    const next = { ...form, [key]: value }
    setForm(next)
    if (attempted) setErrors(validateRegistration(next)) // live re-check once they've tried submitting
  }

  function finish(ready: AuthProfile) {
    flushSync(() => onLogin(ready)) // see Login: routes must exist before navigating
    navigate('/setup', { replace: true })
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setAttempted(true)
    const found = validateRegistration(form)
    setErrors(found)
    setMessage('')
    if (Object.values(found).some(Boolean)) return

    flow.reset()
    setPhase('progress')
    let accountCreated = false
    try {
      tokenStore.clear()
      const tokens = await flow.run(0, () => api.post<Parameters<typeof tokenStore.save>[0]>('/v1/auth/register', {
        institutionName: form.institutionName.trim(),
        adminFullName: form.adminFullName.trim(),
        adminEmail: form.adminEmail.trim(),
        password: form.password,
        type: 'institution',
      }), 800)
      accountCreated = true
      tokenStore.save(tokens)
      const signedIn = await flow.run(1, () => api.profile())
      flow.update(2, { detail: 'Creating an isolated, encrypted database space for your institution.' })
      const result = await flow.run(2, () => waitForWorkspace(signedIn, (s) => flow.update(2, {
        detail: s < 15 ? 'Creating an isolated, encrypted database space for your institution.' : `Still working… ${s}s`,
      })), 900)
      setProfile(result.profile)
      if (!result.ready) {
        setPhase('slow')
        return
      }
      await pause(500)
      finish(result.profile)
    } catch (error) {
      await pause(700)
      if (accountCreated) {
        setPhase('created') // never send them back to a form that would now fail with "already exists"
        return
      }
      tokenStore.clear()
      const text = messageOf(error, 'Registration failed. Please try again.')
      const field = error instanceof ApiError && error.status === 409 ? 'adminEmail' : fieldForServerError(text)
      if (field) setErrors({ ...found, [field]: text })
      else setMessage(text)
      setPhase('form')
    }
  }

  return (
    <Frame eyebrow="CREATE AN INSTITUTION WORKSPACE" title={<>Start with<br /><span className="gradient-text">clarity.</span></>}>
      {phase === 'form' && (
        <>
          <p className="lede">Create the workspace first. Complete your organization profile progressively.</p>
          <form onSubmit={submit} noValidate>
            <FormField label="Institution name" autoComplete="organization" autoFocus value={form.institutionName}
              error={errors.institutionName} onChange={(e) => set('institutionName', e.target.value)} />
            <FormField label="Administrator name" autoComplete="name" value={form.adminFullName}
              error={errors.adminFullName} onChange={(e) => set('adminFullName', e.target.value)} />
            <FormField label="Administrator email" type="email" autoComplete="email" value={form.adminEmail}
              error={errors.adminEmail} hint="Use a work address. You'll sign in with it."
              onChange={(e) => set('adminEmail', e.target.value)} />
            <PasswordField label="Password" autoComplete="new-password" showStrength value={form.password}
              error={errors.password} hint="At least 8 characters, with a letter and a number."
              onChange={(e) => set('password', e.target.value)} />
            <PasswordField label="Confirm password" autoComplete="new-password" value={form.confirmPassword}
              error={errors.confirmPassword} onChange={(e) => set('confirmPassword', e.target.value)} />
            <FormNotice message={message} />
            <SubmitButton>Create institution workspace ↗</SubmitButton>
          </form>
          <div className="auth-foot">Already have an account? <Link to="/login">Sign in</Link></div>
        </>
      )}
      {phase === 'progress' && <ProgressSteps title="SETTING UP YOUR WORKSPACE" steps={flow.steps} />}
      {phase === 'slow' && profile && (
        <ProgressSteps title="SETTING UP YOUR WORKSPACE" steps={flow.steps} footer={
          <div className="progress-footer">
            <div className="notice success">
              Your account is ready. Your workspace is taking longer than usual to finish. You can start your
              organization profile now; verification and wallet features unlock automatically in a minute.
            </div>
            <SubmitButton type="button" onClick={() => finish(profile)}>Continue to setup ↗</SubmitButton>
          </div>
        } />
      )}
      {phase === 'created' && (
        <div className="auth-progress">
          <div className="notice success">Your institution account was created. Sign in to continue.</div>
          <Link className="button button-primary auth-submit" to="/login">Go to sign in ↗</Link>
        </div>
      )}
    </Frame>
  )
}

// ── Accept an agency or team invitation ─────────────────────────────────────

export function AcceptInvite({ kind, Frame }: { kind: 'agency' | 'team'; Frame: Frame }) {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const token = params.get('token') ?? ''
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [errors, setErrors] = useState<{ password?: string | null; confirm?: string | null }>({})
  const [message, setMessage] = useState('')
  const [phase, setPhase] = useState<'form' | 'progress' | 'done'>('form')
  const flow = useSteps(['Activating your account'])

  useEffect(() => {
    if (phase !== 'done') return
    const timer = setTimeout(() => navigate('/login'), 3000)
    return () => clearTimeout(timer)
  }, [phase, navigate])

  async function submit(event: FormEvent) {
    event.preventDefault()
    const found = {
      password: validatePassword(password),
      confirm: confirm === password ? null : "Passwords don't match.",
    }
    setErrors(found)
    setMessage('')
    if (found.password || found.confirm) return

    flow.reset()
    setPhase('progress')
    try {
      await flow.run(0, () => api.post(`/v1/auth/${kind}-invitations/accept`, { token, password }), 900)
      await pause(300)
      setPhase('done')
    } catch (error) {
      await pause(700)
      setMessage(messageOf(error, 'This invitation could not be accepted.'))
      setPhase('form')
    }
  }

  const title = kind === 'agency'
    ? <>Join your<br /><span className="gradient-text">agency workspace.</span></>
    : <>Join your<br /><span className="gradient-text">workspace.</span></>

  return (
    <Frame eyebrow={kind === 'agency' ? 'AGENCY INVITATION' : 'TEAM INVITATION'} title={title}>
      {!token ? (
        <div className="notice error" role="alert">
          This invitation link is incomplete. Open the link from your invitation email again, or ask for a new invitation.
        </div>
      ) : phase === 'form' ? (
        <>
          <p className="lede">Set a password to activate your {kind === 'agency' ? 'agency administrator' : 'team'} account.</p>
          <form onSubmit={submit} noValidate>
            <PasswordField label="Password" autoComplete="new-password" autoFocus showStrength value={password}
              error={errors.password} hint="At least 8 characters, with a letter and a number."
              onChange={(e) => setPassword(e.target.value)} />
            <PasswordField label="Confirm password" autoComplete="new-password" value={confirm}
              error={errors.confirm} onChange={(e) => setConfirm(e.target.value)} />
            <FormNotice message={message} />
            <SubmitButton>Activate account ↗</SubmitButton>
          </form>
        </>
      ) : phase === 'progress' ? (
        <ProgressSteps title="ACCEPTING INVITATION" steps={flow.steps} />
      ) : (
        <div className="auth-progress">
          <div className="notice success" role="status">You're all set. Taking you to sign in…</div>
          <Link className="button button-primary auth-submit" to="/login">Sign in now ↗</Link>
        </div>
      )}
    </Frame>
  )
}
