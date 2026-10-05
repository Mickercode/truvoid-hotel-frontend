import { describe, expect, it } from 'vitest'
import { passwordStrength, validateEmail, validateName, validatePassword } from './validation'

describe('validateEmail', () => {
  it('accepts ordinary addresses', () => {
    expect(validateEmail('name@company.com')).toBeNull()
    expect(validateEmail('  spaced@company.co.uk  ')).toBeNull()
  })

  it('rejects empty, malformed, and multi-@ addresses', () => {
    expect(validateEmail('')).toMatch(/email/i)
    expect(validateEmail('no-at-sign')).not.toBeNull()
    expect(validateEmail('two@@company.com')).not.toBeNull()
    expect(validateEmail('a@.com')).not.toBeNull()
    expect(validateEmail('a@b')).not.toBeNull()
    expect(validateEmail('has space@company.com')).not.toBeNull()
  })
})

describe('validatePassword', () => {
  it('requires 8-128 chars with a letter and a number', () => {
    expect(validatePassword('password1')).toBeNull()
    expect(validatePassword('short1')).toMatch(/8 characters/)
    expect(validatePassword('allletters')).toMatch(/letter and one number/)
    expect(validatePassword('12345678')).toMatch(/letter and one number/)
    expect(validatePassword('a'.repeat(129) + '1')).toMatch(/128/)
  })
})

describe('validateName', () => {
  it('requires 2-120 characters', () => {
    expect(validateName('Ada', 'full name')).toBeNull()
    expect(validateName(' A ', 'full name')).toMatch(/full name/)
    expect(validateName('x'.repeat(121), 'full name')).toMatch(/120/)
  })
})

describe('passwordStrength', () => {
  it('is advisory and never "strong" for an invalid password', () => {
    expect(passwordStrength('').score).toBe(0)
    // 'password' is long enough but fails the letter+number rule, so it is capped at Weak.
    expect(passwordStrength('password').score).toBeLessThanOrEqual(1)
    expect(passwordStrength('password').label).toBe('Weak')
    const strong = passwordStrength('Correct-Horse9Battery!')
    expect(strong.score).toBeGreaterThanOrEqual(3)
  })
})
