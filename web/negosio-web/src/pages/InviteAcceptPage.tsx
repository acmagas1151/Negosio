import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError } from '../api/client'
import { invitationsApi } from '../api/staff'
import { fieldErrorsFrom } from '../lib/formErrors'
import { roleLabel } from '../lib/roles'
import { AuthLayout } from '../components/layout/AuthLayout'
import { Button, Callout, LoadingState, TextField } from '../components/ui'

export default function InviteAcceptPage() {
  const { token = '' } = useParams()
  const navigate = useNavigate()

  const preview = useQuery({
    queryKey: ['invitation', token],
    queryFn: () => invitationsApi.preview(token),
    retry: false,
  })

  const [firstName, setFirstName] = useState('')
  const [lastName, setLastName] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState('')

  const accept = useMutation({
    mutationFn: () => invitationsApi.accept(token, { firstName: firstName.trim(), lastName: lastName.trim(), password }),
    onSuccess: (result) => {
      navigate('/login', { replace: true, state: { registeredEmail: result.email } })
    },
    onError: (err) => {
      const fields = fieldErrorsFrom(err)
      if (Object.keys(fields).length > 0) {
        setErrors(fields)
        return
      }
      setFormError(err instanceof ApiError ? err.message : 'Could not accept the invitation.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (accept.isPending) return
    const next: Record<string, string> = {}
    if (!firstName.trim()) next.firstname = 'First name is required.'
    if (!lastName.trim()) next.lastname = 'Last name is required.'
    if (password.length < 8) next.password = 'Password must be at least 8 characters.'
    if (password !== confirm) next.confirm = 'Passwords do not match.'
    setErrors(next)
    setFormError('')
    if (Object.keys(next).length > 0) return
    accept.mutate()
  }

  if (preview.isPending) {
    return (
      <AuthLayout title="Join the team" subtitle="Checking your invitation…">
        <LoadingState />
      </AuthLayout>
    )
  }

  if (preview.isError || !preview.data) {
    const message =
      preview.error instanceof ApiError
        ? preview.error.message
        : 'This invitation link is not valid.'
    return (
      <AuthLayout
        title="Invitation problem"
        subtitle="We couldn’t open this invitation."
        footer={
          <>
            Already have an account? <Link to="/login">Sign in</Link>
          </>
        }
      >
        <Callout>{message}</Callout>
      </AuthLayout>
    )
  }

  const { businessName, email, role } = preview.data

  return (
    <AuthLayout
      title={`Join ${businessName}`}
      subtitle={`You've been invited as ${roleLabel(role)}. Set your details to finish.`}
      footer={
        <>
          Already have an account? <Link to="/login">Sign in</Link>
        </>
      }
    >
      {formError && <Callout>{formError}</Callout>}

      <form onSubmit={submit} noValidate>
        <TextField label="Email" name="email" type="email" value={email} readOnly disabled />
        <div className="grid gap-x-3 sm:grid-cols-2">
          <TextField
            label="First name"
            name="firstName"
            value={firstName}
            onChange={(e) => setFirstName(e.target.value)}
            error={errors.firstname}
            autoFocus
          />
          <TextField
            label="Last name"
            name="lastName"
            value={lastName}
            onChange={(e) => setLastName(e.target.value)}
            error={errors.lastname}
          />
        </div>
        <TextField
          label="Password"
          name="password"
          type="password"
          autoComplete="new-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={errors.password}
          hint="8+ chars with upper & lowercase, a number and a symbol."
        />
        <TextField
          label="Confirm password"
          name="confirm"
          type="password"
          autoComplete="new-password"
          value={confirm}
          onChange={(e) => setConfirm(e.target.value)}
          error={errors.confirm}
        />
        <Button type="submit" block loading={accept.isPending} className="mt-2">
          {accept.isPending ? 'Setting up…' : 'Accept invitation'}
        </Button>
      </form>
    </AuthLayout>
  )
}
