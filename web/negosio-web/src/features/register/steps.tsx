import type { ReactNode } from 'react'
import { Check } from 'lucide-react'
import { businessTypeLabel, businessTypeOptions } from '../../lib/businessTypes'
import { cn } from '../../lib/cn'
import { Badge, Card, TextField } from '../../components/ui'
import type { StepErrors, WizardData } from './wizard'

interface StepProps {
  data: WizardData
  errors: StepErrors
  update: (patch: Partial<WizardData>) => void
}

export function BusinessTypeStep({ data, errors, update }: StepProps) {
  return (
    <div>
      <p className="mb-4 text-sm text-text-secondary">
        What kind of business are you setting up?
      </p>
      <div className="grid gap-3">
        {businessTypeOptions.map((option) => {
          const Icon = option.icon
          const selected = data.businessType === option.value
          return (
            <button
              type="button"
              key={option.value}
              className={cn(
                'flex items-start gap-4 rounded-xl border p-4 text-left transition-colors',
                'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500',
                option.available && !selected && 'border-border bg-white hover:border-primary-300',
                selected && 'border-primary-500 bg-[#f8fbff]',
                !option.available && 'cursor-not-allowed border-border bg-surface-subtle opacity-70',
              )}
              aria-pressed={selected}
              aria-disabled={!option.available || undefined}
              disabled={!option.available}
              onClick={() => update({ businessType: option.value })}
            >
              <span
                className={cn(
                  'flex size-11 shrink-0 items-center justify-center rounded-lg',
                  selected ? 'bg-primary-100 text-primary-600' : option.accentClass,
                )}
              >
                <Icon className="size-5" aria-hidden="true" />
              </span>

              <span className="min-w-0 flex-1">
                <span className="flex items-center gap-2">
                  <span className="font-semibold text-text-primary">{option.title}</span>
                  {option.badge && <Badge tone="neutral">{option.badge}</Badge>}
                </span>
                <span className="mt-0.5 block text-[13px] text-text-secondary">
                  {option.description}
                </span>
              </span>

              <span
                className={cn(
                  'mt-0.5 flex size-5 shrink-0 items-center justify-center rounded-full border',
                  selected
                    ? 'border-primary-600 bg-primary-600 text-white'
                    : 'border-border-strong bg-white',
                )}
                aria-hidden="true"
              >
                {selected && <Check className="size-3.5" />}
              </span>
            </button>
          )
        })}
      </div>
      {errors['businessType'] && (
        <p className="mt-2 text-[13px] text-danger">{errors['businessType']}</p>
      )}
    </div>
  )
}

export function BusinessInfoStep({ data, errors, update }: StepProps) {
  return (
    <div>
      <TextField
        label="Business Name"
        name="businessName"
        value={data.businessName}
        error={errors['businessName']}
        autoFocus
        onChange={(e) => update({ businessName: e.target.value })}
      />
    </div>
  )
}

export function BranchStep({ data, errors, update }: StepProps) {
  const setBranch = (patch: Partial<WizardData['branch']>) =>
    update({ branch: { ...data.branch, ...patch } })
  return (
    <div className="space-y-1">
      <div className="grid gap-x-4 sm:grid-cols-2">
        <TextField
          label="Branch Name"
          name="branch.name"
          value={data.branch.name}
          error={errors['branch.name']}
          onChange={(e) => setBranch({ name: e.target.value })}
        />
        <TextField
          label="Branch Code"
          name="branch.code"
          value={data.branch.code}
          hint="Unique within your business, e.g. MAIN"
          error={errors['branch.code']}
          onChange={(e) => setBranch({ code: e.target.value.toUpperCase() })}
        />
      </div>
      <TextField
        label="Address Line 1"
        name="branch.addressLine1"
        value={data.branch.addressLine1}
        error={errors['branch.addressLine1']}
        onChange={(e) => setBranch({ addressLine1: e.target.value })}
      />
      <TextField
        label="Address Line 2 (optional)"
        name="branch.addressLine2"
        value={data.branch.addressLine2}
        onChange={(e) => setBranch({ addressLine2: e.target.value })}
      />
      <div className="grid gap-x-4 sm:grid-cols-2">
        <TextField
          label="City"
          name="branch.city"
          value={data.branch.city}
          error={errors['branch.city']}
          onChange={(e) => setBranch({ city: e.target.value })}
        />
        <TextField
          label="Province"
          name="branch.province"
          value={data.branch.province}
          error={errors['branch.province']}
          onChange={(e) => setBranch({ province: e.target.value })}
        />
      </div>
      <TextField
        label="Postal Code (optional)"
        name="branch.postalCode"
        value={data.branch.postalCode}
        onChange={(e) => setBranch({ postalCode: e.target.value })}
      />
    </div>
  )
}

export function OwnerStep({ data, errors, update }: StepProps) {
  const setOwner = (patch: Partial<WizardData['owner']>) =>
    update({ owner: { ...data.owner, ...patch } })
  return (
    <div className="space-y-1">
      <div className="grid gap-x-4 sm:grid-cols-2">
        <TextField
          label="First Name"
          name="owner.firstName"
          value={data.owner.firstName}
          error={errors['owner.firstName']}
          onChange={(e) => setOwner({ firstName: e.target.value })}
        />
        <TextField
          label="Last Name"
          name="owner.lastName"
          value={data.owner.lastName}
          error={errors['owner.lastName']}
          onChange={(e) => setOwner({ lastName: e.target.value })}
        />
      </div>
      <TextField
        label="Email"
        name="owner.email"
        type="email"
        autoComplete="email"
        value={data.owner.email}
        error={errors['owner.email']}
        onChange={(e) => setOwner({ email: e.target.value })}
      />
      <div className="grid gap-x-4 sm:grid-cols-2">
        <TextField
          label="Password"
          name="owner.password"
          type="password"
          autoComplete="new-password"
          value={data.owner.password}
          error={errors['owner.password']}
          onChange={(e) => setOwner({ password: e.target.value })}
        />
        <TextField
          label="Confirm Password"
          name="owner.confirmPassword"
          type="password"
          autoComplete="new-password"
          value={data.owner.confirmPassword}
          error={errors['owner.confirmPassword']}
          onChange={(e) => setOwner({ confirmPassword: e.target.value })}
        />
      </div>
    </div>
  )
}

export function ReviewStep({ data }: StepProps) {
  const rows: { label: string; value: ReactNode }[] = [
    { label: 'Business', value: data.businessName || '—' },
    {
      label: 'Business Type',
      value: data.businessType ? businessTypeLabel(data.businessType) : '—',
    },
    {
      label: 'Branch',
      value: (
        <>
          {data.branch.name} ({data.branch.code})
          <br />
          {data.branch.addressLine1}
          {data.branch.addressLine2 ? `, ${data.branch.addressLine2}` : ''}
          <br />
          {[data.branch.city, data.branch.province, data.branch.postalCode]
            .filter(Boolean)
            .join(', ')}
        </>
      ),
    },
    {
      label: 'Owner',
      value: (
        <>
          {data.owner.firstName} {data.owner.lastName}
          <br />
          {data.owner.email}
        </>
      ),
    },
  ]

  return (
    <Card padding="none" className="divide-y divide-border-light">
      {rows.map((row) => (
        <div key={row.label} className="grid gap-1 p-4 sm:grid-cols-[140px_1fr] sm:gap-4">
          <dt className="text-xs font-semibold uppercase tracking-wide text-text-muted">
            {row.label}
          </dt>
          <dd className="m-0 text-sm text-text-primary">{row.value}</dd>
        </div>
      ))}
    </Card>
  )
}
