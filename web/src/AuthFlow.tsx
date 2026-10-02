import { InputHTMLAttributes, KeyboardEvent, ReactNode, useId, useState } from 'react'
import { api, AuthProfile } from './api'
import { passwordStrength } from './validation'

// ── Form pieces ──────────────────────────────────────────────────────────────

type FieldProps = {
  label: string
  error?: string | null
  hint?: ReactNode
} & InputHTMLAttributes<HTMLInputElement>

export function FormField({ label, error, hint, id, ...props }: FieldProps) {
  const generated = useId()
  const inputId = id ?? generated
  const messageId = `${inputId}-message`
  return (
    <div className={`auth-field${error ? ' invalid' : ''}`}>
      <label htmlFor={inputId}>{label}</label>
      <input
        id={inputId}
        aria-invalid={error ? true : undefined}
        aria-describedby={error || hint ? messageId : undefined}
        {...props}
      />
      {error
        ? <span className="field-error" id={messageId} role="alert">{error}</span>
        : hint ? <span className="field-hint" id={messageId}>{hint}</span> : null}
    </div>
  )
}

export function PasswordField({
  showStrength = false,
  ...props
}: FieldProps & { showStrength?: boolean }) {
  const [visible, setVisible] = useState(false)
  const [capsLock, setCapsLock] = useState(false)
  const strength = passwordStrength(String(props.value ?? ''))
  const watchCaps = (event: KeyboardEvent<HTMLInputElement>) =>
    setCapsLock(event.getModifierState?.('CapsLock') ?? false)

  return (
    <div className="password-field">
      <FormField
        {...props}
        type={visible ? 'text' : 'password'}
        onKeyUp={watchCaps}
        onKeyDown={watchCaps}
        hint={capsLock ? 'Caps Lock is on.' : props.hint}
      />
      <button
        type="button"
        className="reveal-toggle"
        onClick={() => setVisible((v) => !v)}
        aria-label={visible ? 'Hide password' : 'Show password'}
        aria-pressed={visible}
      >
        {visible ? 'HIDE' : 'SHOW'}
      </button>
      {showStrength && strength.score > 0 && (
        <div className={`strength strength-${strength.score}`} aria-live="polite">
          <div className="strength-bars">{[1, 2, 3, 4].map((i) => <span key={i} />)}</div>
          <span className="strength-label">{strength.label}</span>
        </div>
      )}
    </div>
  )
}

export function SubmitButton({ busy, children, busyLabel, ...props }: {
  busy?: boolean
  busyLabel?: string
} & React.ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button className="button button-primary auth-submit" disabled={busy || props.disabled} aria-busy={busy} {...props}>
      {busy ? <><span className="spinner" aria-hidden="true" />{busyLabel ?? 'Working…'}</> : children}
    </button>
  )
}

// ── Progress steps ───────────────────────────────────────────────────────────

export type StepState = 'waiting' | 'active' | 'done' | 'error'
export type Step = { label: string; detail?: string; state: StepState }

export function ProgressSteps({ title, steps, footer }: { title: string; steps: Step[]; footer?: ReactNode }) {
  return (
    <div className="auth-progress" role="status" aria-live="polite">
      <div className="eyebrow">{title}</div>
      <ol>
        {steps.map((step) => (
          <li key={step.label} className={`auth-step ${step.state}`}>
            <span className="step-icon" aria-hidden="true">
              {step.state === 'done' ? '✓' : step.state === 'error' ? '!' : step.state === 'active' ? <span className="spinner" /> : ''}
            </span>
            <span className="step-text">
              <strong>{step.label}</strong>
              {step.detail && <small>{step.detail}</small>}
            </span>
          </li>
        ))}
      </ol>
      {footer}
    </div>
  )
}

/** Drives a list of steps: marks each active → done (or error) as its task runs. */
export function useSteps(labels: string[]) {
  const [steps, setSteps] = useState<Step[]>(labels.map((label) => ({ label, state: 'waiting' })))
  const update = (index: number, patch: Partial<Step>) =>
    setSteps((current) => current.map((s, i) => (i === index ? { ...s, ...patch } : s)))

  async function run<T>(index: number, task: () => Promise<T>, minimumMs = 450): Promise<T> {
    update(index, { state: 'active' })
    try {
      const result = await atLeast(task(), minimumMs)
      update(index, { state: 'done' })
      return result
    } catch (error) {
      update(index, { state: 'error' })
      throw error
    }
  }

  const reset = () => setSteps(labels.map((label) => ({ label, state: 'waiting' })))
  return { steps, run, update, reset }
}

/**
 * Resolves no sooner than `ms`. Real work still runs at full speed; this only stops
 * a step that finishes instantly from flashing past before anyone can read it.
 */
export async function atLeast<T>(work: Promise<T>, ms: number): Promise<T> {
  const [result] = await Promise.all([work, new Promise((r) => setTimeout(r, ms))])
  return result
}

/**
 * New Organizations stay 'pending' until the provisioning worker creates their
 * database schema (normally a few seconds). Polls /v1/auth/me until it's active.
 * Resolves with the latest profile; `ready` is false if it timed out.
 */
export async function waitForWorkspace(
  initial: AuthProfile,
  onWait?: (seconds: number) => void,
  timeoutMs = 60_000,
): Promise<{ profile: AuthProfile; ready: boolean }> {
  let profile = initial
  const started = Date.now()
  while (profile.organizationStatus === 'pending') {
    const elapsed = Date.now() - started
    if (elapsed >= timeoutMs) return { profile, ready: false }
    onWait?.(Math.round(elapsed / 1000))
    await new Promise((r) => setTimeout(r, 2000))
    profile = await api.profile()
  }
  return { profile, ready: true }
}
