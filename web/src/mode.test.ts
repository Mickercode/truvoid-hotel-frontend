import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getMode, setMode } from './mode'

describe('mode', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  it('defaults to test when nothing is stored', () => {
    expect(getMode()).toBe('test')
  })

  it('defaults to test for any unrecognised stored value', () => {
    localStorage.setItem('truvoid_mode', 'production')
    expect(getMode()).toBe('test')
  })

  it('persists live and notifies listeners', () => {
    const listener = vi.fn()
    window.addEventListener('truvoid:mode-changed', listener)
    setMode('live')
    expect(getMode()).toBe('live')
    expect(listener).toHaveBeenCalledTimes(1)
    window.removeEventListener('truvoid:mode-changed', listener)
  })
})
