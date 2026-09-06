import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { staffApi } from '../../api/staff'
import type { StaffMemberDto } from '../../api/types'
import { roleLabel } from '../../lib/roles'
import { Button, Callout, Modal, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  member: StaffMemberDto | null
}

type PermissionKey = 'salesVoid' | 'salesReturn' | 'discountApply' | 'cashDrawerOpen'

interface PermissionDef {
  key: PermissionKey
  label: string
  description: string
}

// Every entry here has a real, server-enforced grant-or-approval path (VoidAuthorizationResolver,
// ReturnAuthorizationResolver, CheckoutService.EnsureDiscountAuthorizedAsync,
// CashDrawerService.ResolveAuthorizationAsync) — this list is deliberately not a wish list of every
// button in the POS; a toggle only exists here because a matching backend check exists.
const SALES_PERMISSIONS: PermissionDef[] = [
  {
    key: 'salesVoid',
    label: 'Void sale',
    description: 'Allow this cashier to void a completed sale without supervisor approval.',
  },
  {
    key: 'salesReturn',
    label: 'Return sale',
    description: 'Allow this cashier to process returns without supervisor approval.',
  },
  {
    key: 'discountApply',
    label: 'Apply discount',
    description: 'Allow this cashier to apply protected discounts without supervisor approval.',
  },
]

const CASH_PERMISSIONS: PermissionDef[] = [
  {
    key: 'cashDrawerOpen',
    label: 'Open cash drawer',
    description: 'Allow this cashier to open the cash drawer without supervisor approval.',
  },
]

type Values = Record<PermissionKey, boolean>

function Toggle({
  checked,
  onChange,
  disabled,
  label,
}: {
  checked: boolean
  onChange: (next: boolean) => void
  disabled?: boolean
  label: string
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500 disabled:cursor-not-allowed disabled:opacity-50 ${
        checked ? 'bg-primary-600' : 'bg-border-strong'
      }`}
    >
      <span
        aria-hidden="true"
        className={`inline-block size-4 transform rounded-full bg-white transition-transform ${
          checked ? 'translate-x-6' : 'translate-x-1'
        }`}
      />
    </button>
  )
}

function PermissionGroup({
  title,
  permissions,
  values,
  onToggle,
  disabled,
}: {
  title: string
  permissions: PermissionDef[]
  values: Values
  onToggle: (key: PermissionKey, next: boolean) => void
  disabled: boolean
}) {
  return (
    <section>
      <h3 className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-text-muted">{title}</h3>
      <div className="divide-y divide-border rounded-xl border border-border">
        {permissions.map((p) => (
          <div key={p.key} className="flex items-start justify-between gap-4 p-3">
            <div className="min-w-0">
              <p className="text-sm font-medium text-text-primary">{p.label}</p>
              <p className="mt-0.5 text-[12px] text-text-muted">{p.description}</p>
            </div>
            <Toggle
              checked={values[p.key]}
              onChange={(next) => onToggle(p.key, next)}
              disabled={disabled}
              label={p.label}
            />
          </div>
        ))}
      </div>
    </section>
  )
}

/**
 * Owner/Admin/Manager (the roles admitted by StaffPermissionManage) grant/revoke a Cashier's direct
 * overrides here. These are per-user overrides layered on top of role, never the reverse — Owner /
 * Admin / Manager rows are never offered this modal (see StaffPage), since they already bypass every
 * one of these checks by role and holding a "grant" for them would be meaningless.
 */
export function StaffPermissionsModal({ open, onClose, member }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  const [values, setValues] = useState<Values>({
    salesVoid: false,
    salesReturn: false,
    discountApply: false,
    cashDrawerOpen: false,
  })
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open || !member) return
    // oxlint-disable-next-line set-state-in-effect
    setValues({
      salesVoid: member.salesVoid,
      salesReturn: member.salesReturn,
      discountApply: member.discountApply,
      cashDrawerOpen: member.cashDrawerOpen,
    })
    setError('')
  }, [open, member])

  const mutation = useMutation({
    mutationFn: () => staffApi.setPermissions(member!.id, values),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['staff'] })
      toast('success', 'Permissions updated')
      onClose()
    },
    onError: (err) => {
      // Keep the modal open with a useful error — never silently lose what was toggled.
      setError(err instanceof ApiError ? err.message : 'Could not update permissions.')
    },
  })

  if (!member) return null

  const name = `${member.firstName ?? ''} ${member.lastName ?? ''}`.trim() || member.email
  const unchanged =
    values.salesVoid === member.salesVoid &&
    values.salesReturn === member.salesReturn &&
    values.discountApply === member.discountApply &&
    values.cashDrawerOpen === member.cashDrawerOpen

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Permissions"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => mutation.mutate()}
            loading={mutation.isPending}
            disabled={unchanged}
          >
            Save changes
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      <p className="mb-4 text-sm text-text-secondary">
        Manage additional permissions for <span className="font-semibold text-text-primary">{name}</span>
        <br />
        <span className="text-[13px] text-text-muted">
          {roleLabel(member.role)}
          {member.branchName ? ` · ${member.branchName}` : ''}
        </span>
      </p>

      <div className="space-y-4">
        <PermissionGroup
          title="Sales"
          permissions={SALES_PERMISSIONS}
          values={values}
          disabled={mutation.isPending}
          onToggle={(key, next) => setValues((prev) => ({ ...prev, [key]: next }))}
        />
        <PermissionGroup
          title="Cash operations"
          permissions={CASH_PERMISSIONS}
          values={values}
          disabled={mutation.isPending}
          onToggle={(key, next) => setValues((prev) => ({ ...prev, [key]: next }))}
        />
      </div>
    </Modal>
  )
}
