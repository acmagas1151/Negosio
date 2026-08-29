import { useState } from 'react'
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom'
import { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { AuthLayout } from '../components/layout/AuthLayout'
import { Button, Callout, TextField } from '../components/ui'

interface LocationState {
  registeredEmail?: string
  from?: string
}

export default function LoginPage() {
  const { login, isAuthenticated, isInitializing } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const state = (location.state ?? {}) as LocationState

  const [email, setEmail] = useState(state.registeredEmail ?? '')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  if (!isInitializing && isAuthenticated) {
    return <Navigate to="/dashboard" replace />
  }

  const onSubmit = async (event: React.FormEvent) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await login({ email, password })
      navigate(state.from ?? '/dashboard', { replace: true })
    } catch (err) {
      setError(
        err instanceof ApiError
          ? err.message
          : 'Something went wrong while signing in. Please try again.',
      )
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <AuthLayout
      title="Sign in"
      subtitle="Welcome back. Enter your details to continue."
      footer={
        <>
          New to Negosio? <Link to="/register">Create your business</Link>
        </>
      }
    >
      {state.registeredEmail && (
        <Callout tone="success">Your business is ready. Sign in to continue.</Callout>
      )}
      {error && <Callout>{error}</Callout>}

      <form onSubmit={onSubmit} noValidate>
        <TextField
          label="Email"
          name="email"
          type="email"
          autoComplete="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />
        <TextField
          label="Password"
          name="password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />
        <Button type="submit" block loading={submitting} className="mt-2">
          {submitting ? 'Signing in…' : 'Sign in'}
        </Button>
      </form>
    </AuthLayout>
  )
}
