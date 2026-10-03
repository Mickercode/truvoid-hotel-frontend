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

/** Current mode, forced to test when the workspace can't go live yet. */
export function useMode(liveEnabled: boolean): [Mode, (mode: Mode) => void] {
  const [mode, setState] = useState<Mode>(getMode())
  useEffect(() => {
    const sync = () => setState(getMode())
    window.addEventListener(EVENT, sync)
    window.addEventListener('storage', sync) // other tabs
    return () => { window.removeEventListener(EVENT, sync); window.removeEventListener('storage', sync) }
  }, [])
  useEffect(() => { if (!liveEnabled && mode === 'live') setMode('test') }, [liveEnabled, mode])
  return [liveEnabled ? mode : 'test', setMode]
}
