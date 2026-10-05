import { useEffect, useState } from 'react'

export type Mode = 'test' | 'live'

const KEY = 'truvoid_mode'
const EVENT = 'truvoid:mode-changed'

/**
 * The dashboard's Test/Live switch. Sent to the API as X-TruvoID-Mode; the server
 * still refuses live for workspaces that aren't approved, so this is a preference,
 * not a permission. Anything unset is test — live is never the accidental default.
 */
export function getMode(): Mode {
  return localStorage.getItem(KEY) === 'live' ? 'live' : 'test'
}

export function setMode(mode: Mode) {
  localStorage.setItem(KEY, mode)
  window.dispatchEvent(new Event(EVENT))
}

/** Current mode. The Test/Live switch is always toggleable; the server still refuses
 *  live calls for a workspace that isn't approved, so this stays a preference, not a
 *  permission, and the shell warns when Live is picked too early. */
export function useMode(): [Mode, (mode: Mode) => void] {
  const [mode, setState] = useState<Mode>(getMode())
  useEffect(() => {
    const sync = () => setState(getMode())
    window.addEventListener(EVENT, sync)
    window.addEventListener('storage', sync) // other tabs
    return () => { window.removeEventListener(EVENT, sync); window.removeEventListener('storage', sync) }
  }, [])
  return [mode, setMode]
}
