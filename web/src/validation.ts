// Mirrors src/TruvoID.API/Endpoints/AuthValidation.cs so users get instant feedback.
// The server re-checks everything; keep the two in step.

export function validateEmail(value: string): string | null {
  const email = value.trim()
  if (!email) return 'Enter your email address.'
  const at = email.indexOf('@')
  const valid = email.length <= 254 && at > 0 && at === email.lastIndexOf('@')
    && email.indexOf('.', at) > at + 1 && !email.endsWith('.') && !/\s/.test(email)
  return valid ? null : 'Enter a valid email address, like name@company.com.'
}

export function validatePassword(value: string): string | null {
  if (value.length < 8) return 'Use at least 8 characters.'
  if (value.length > 128) return 'Use at most 128 characters.'
  if (!/\p{L}/u.test(value) || !/\d/.test(value)) return 'Include at least one letter and one number.'
  return null
}

export function validateName(value: string, label: string): string | null {
  const name = value.trim()
  if (name.length < 2) return `Enter the ${label}.`
  if (name.length > 120) return `The ${label} must be 120 characters or fewer.`
  return null
}

export type Strength = { score: 0 | 1 | 2 | 3 | 4; label: string }

// Advisory only: rewards length and variety. The hard rule is validatePassword.
export function passwordStrength(value: string): Strength {
  if (!value) return { score: 0, label: '' }
  let score = 0
  if (value.length >= 8) score++
  if (value.length >= 12) score++
  if (/[a-z]/.test(value) && /[A-Z]/.test(value)) score++
  if (/\d/.test(value) && /[^\p{L}\d]/u.test(value)) score++
  if (validatePassword(value)) score = Math.min(score, 1)
  const labels = ['Too weak', 'Weak', 'Fair', 'Good', 'Strong']
  return { score: score as Strength['score'], label: labels[score] }
}
