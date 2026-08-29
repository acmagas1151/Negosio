import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ArrowLeft, ArrowRight } from 'lucide-react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError } from '../api/client'
import { authApi } from '../api/endpoints'
import { AuthLayout } from '../components/layout/AuthLayout'
import { Button, Callout, Stepper } from '../components/ui'
import {
  BranchStep,
  BusinessInfoStep,
  BusinessTypeStep,
  OwnerStep,
  ReviewStep,
} from '../features/register/steps'
import {
  STEP_TITLES,
  emptyWizardData,
  firstStepWithError,
  toRegisterRequest,
  validateStep,
} from '../features/register/wizard'
import type { StepErrors, WizardData } from '../features/register/wizard'

const STEP_COMPONENTS = [BusinessTypeStep, BusinessInfoStep, BranchStep, OwnerStep, ReviewStep]
const LAST_STEP = STEP_COMPONENTS.length - 1

const STEPPER_LABELS = [
  'Business Type',
  'Business Info',
  'First Branch',
  'Owner Account',
  'Review',
] as const

export default function RegisterPage() {
  const navigate = useNavigate()
  const [step, setStep] = useState(0)
  const [data, setData] = useState<WizardData>(emptyWizardData)
  const [errors, setErrors] = useState<StepErrors>({})

  const mutation = useMutation({
    mutationFn: () => authApi.register(toRegisterRequest(data)),
    onSuccess: () => {
      navigate('/login', { replace: true, state: { registeredEmail: data.owner.email } })
    },
    onError: (error) => {
      if (error instanceof ApiError && error.fieldErrors) {
        const flattened: StepErrors = {}
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          flattened[key] = messages.join(' ')
        }
        setErrors(flattened)
        const target = firstStepWithError(Object.keys(flattened))
        if (target !== null) setStep(target)
      }
    },
  })

  const update = (patch: Partial<WizardData>) => {
    setData((prev) => ({ ...prev, ...patch }))
    mutation.reset()
  }

  const goNext = () => {
    const stepErrors = validateStep(step, data)
    setErrors(stepErrors)
    if (Object.keys(stepErrors).length > 0) return
    setStep((s) => Math.min(s + 1, LAST_STEP))
  }

  const goBack = () => {
    setErrors({})
    mutation.reset()
    setStep((s) => Math.max(s - 1, 0))
  }

  const submit = () => {
    // Re-run every step's validation before the final call.
    const allErrors = [0, 1, 2, 3].reduce<StepErrors>(
      (acc, s) => ({ ...acc, ...validateStep(s, data) }),
      {},
    )
    setErrors(allErrors)
    const target = firstStepWithError(Object.keys(allErrors))
    if (target !== null) {
      setStep(target)
      return
    }
    mutation.mutate()
  }

  const StepComponent = STEP_COMPONENTS[step]
  const serverError =
    mutation.error instanceof ApiError && !mutation.error.fieldErrors ? mutation.error.message : null

  return (
    <AuthLayout
      title="Create your business"
      subtitle="Set up your workspace in a few steps."
      width="lg"
      footer={
        <>
          Already have an account? <Link to="/login">Sign in</Link>
        </>
      }
    >
      <div className="space-y-6">
        <Stepper steps={STEPPER_LABELS} current={step} />

        <div>
          <h2 className="text-lg font-semibold text-text-primary">{STEP_TITLES[step]}</h2>
        </div>

        {serverError && <Callout>{serverError}</Callout>}

        <StepComponent data={data} errors={errors} update={update} />

        <div className="flex items-center justify-between border-t border-border-light pt-4">
          {step > 0 ? (
            <Button
              variant="secondary"
              onClick={goBack}
              disabled={mutation.isPending}
              leadingIcon={<ArrowLeft className="size-4" aria-hidden="true" />}
            >
              Back
            </Button>
          ) : (
            <span />
          )}

          {step < LAST_STEP ? (
            <Button
              onClick={goNext}
              trailingIcon={<ArrowRight className="size-4" aria-hidden="true" />}
            >
              Continue
            </Button>
          ) : (
            <Button onClick={submit} loading={mutation.isPending}>
              {mutation.isPending ? 'Creating…' : 'Create Business'}
            </Button>
          )}
        </div>
      </div>
    </AuthLayout>
  )
}
