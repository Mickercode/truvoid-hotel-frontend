import { ReactNode, useState } from 'react'

/**
 * A button that swaps to an inline confirm step before running a destructive action,
 * so revoke/suspend/disable can't happen on a single stray click.
 */
export function ConfirmButton({
  label,
  question = 'Are you sure?',
  confirmLabel = 'Yes, confirm',
  cancelLabel = 'Cancel',
  onConfirm,
  disabled,
  className = 'link-button',
}: {
  label: ReactNode
  question?: string
  confirmLabel?: string
  cancelLabel?: string
  onConfirm: () => void
  disabled?: boolean
  className?: string
}) {
  const [confirming, setConfirming] = useState(false)

  if (!confirming) {
    return (
      <button type="button" className={className} disabled={disabled} onClick={() => setConfirming(true)}>
        {label}
      </button>
    )
  }

  return (
    <span className="confirm-inline" role="group" aria-label={question}>
      <span className="confirm-question">{question}</span>
      <button type="button" className="link-button button-danger" disabled={disabled}
        onClick={() => { setConfirming(false); onConfirm() }}>
        {confirmLabel}
      </button>
      <button type="button" className="link-button" disabled={disabled} onClick={() => setConfirming(false)}>
        {cancelLabel}
      </button>
    </span>
  )
}
