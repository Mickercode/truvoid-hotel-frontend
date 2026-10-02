import { useEffect, useState } from 'react'
import { api } from './api'

export type DeploymentEnvironment = 'live' | 'sandbox'

// One /health call per page load, shared by every component that asks.
let pending: Promise<DeploymentEnvironment> | null = null

function loadEnvironment(): Promise<DeploymentEnvironment> {
  pending ??= api.get<{ environment?: string }>('/health')
    .then((h) => (h.environment === 'sandbox' ? 'sandbox' : 'live') as DeploymentEnvironment)
    .catch(() => 'live' as const) // fail towards "live": never tell someone real calls are fake
  return pending
}

/** null while loading. */
export function useEnvironment(): DeploymentEnvironment | null {
  const [environment, setEnvironment] = useState<DeploymentEnvironment | null>(null)
  useEffect(() => {
    let active = true
    void loadEnvironment().then((e) => { if (active) setEnvironment(e) })
    return () => { active = false }
  }, [])
  return environment
}
