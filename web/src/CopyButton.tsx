import { useState } from 'react'

export function CopyButton({ value, label = 'Copy' }: { value: string; label?: string }) {
  const [copied, setCopied] = useState(false)
  const [failed, setFailed] = useState(false)

  async function copy() {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setFailed(false)
      window.setTimeout(() => setCopied(false), 1800)
    } catch {
      setFailed(true)
    }
  }

  return <button className="copy-button" type="button" onClick={() => void copy()}>{failed ? 'Copy failed' : copied ? 'Copied' : label}</button>
}
