import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { branchesApi } from '../../api/branches'
import { receiptSettingsApi } from '../../api/receiptSettings'
import { settingsApi } from '../../api/settings'
import type { ReceiptSettingsDto, UpdateReceiptSettingsRequest } from '../../api/types'
import { RECEIPT_SETTINGS_QUERY_KEY, useReceiptSettings } from '../../hooks/useReceiptSettings'
import { fieldErrorsFrom } from '../../lib/formErrors'
import { cn } from '../../lib/cn'
import { useCan } from '../../lib/useCan'
import { DashboardLayout } from '../../components/layout/DashboardLayout'
import { SettingsTabs } from '../../components/settings/SettingsTabs'
import { ReceiptPreview } from '../../components/settings/ReceiptPreview'
import {
  Button,
  Callout,
  Card,
  ConfirmDialog,
  ErrorState,
  LoadingState,
  PageHeader,
  Select,
  TextArea,
  TextField,
  useToast,
} from '../../components/ui'

export default function ReceiptSettingsPage() {
  return (
    <DashboardLayout title="Receipt settings">
      <div className="space-y-6">
        <PageHeader
          title="Receipt settings"
          description="Customize the information and appearance shown on printed receipts."
        />
        <SettingsTabs />
        <ReceiptSettingsForm />
      </div>
    </DashboardLayout>
  )
}

const TENANT_SCOPE = 'tenant'

type SubTab = 'general' | 'sales' | 'delivery'

function toFormValues(d: ReceiptSettingsDto): UpdateReceiptSettingsRequest {
  return {
    width: d.width,
    salesHeaderText: d.salesHeaderText,
    salesFooterText: d.salesFooterText,
    salesShowBranch: d.salesShowBranch,
    salesShowCashier: d.salesShowCashier,
    salesShowPaymentMethod: d.salesShowPaymentMethod,
    salesShowTaxLine: d.salesShowTaxLine,
    salesShowReferenceNumber: d.salesShowReferenceNumber,
    deliveryHeaderText: d.deliveryHeaderText,
    deliveryFooterText: d.deliveryFooterText,
    deliveryShowPrices: d.deliveryShowPrices,
    deliveryShowRelatedSaleNumber: d.deliveryShowRelatedSaleNumber,
    deliveryShowContactNumber: d.deliveryShowContactNumber,
    deliveryShowSignatureFields: d.deliveryShowSignatureFields,
  }
}

function ReceiptSettingsForm() {
  const qc = useQueryClient()
  const { toast } = useToast()
  const canWrite = useCan('settings:write')

  const [scope, setScope] = useState<string>(TENANT_SCOPE)
  const selectedBranchId = scope === TENANT_SCOPE ? undefined : scope
  const [tab, setTab] = useState<SubTab>('general')

  const receiptSettings = useReceiptSettings(selectedBranchId)
  const branchesQuery = useQuery({
    queryKey: ['branches', 'receipt-settings-scope'],
    queryFn: () => branchesApi.list({ includeInactive: true }),
  })
  const businessInfo = useQuery({
    queryKey: ['settings', 'business-info'],
    queryFn: settingsApi.getBusinessInfo,
  })

  const branches = branchesQuery.data ?? []
  // Inactive branches can't be saved (ResolveTargetBranchAsync runs with allowInactive: false),
  // so don't offer them as scope options.
  const selectableBranches = branches.filter((b) => b.isActive)
  const canEditReceipts = receiptSettings.data?.canEdit ?? false

  // A non-writer (Manager) can't edit the tenant default; their branches query only
  // returns their own branch(es). Land them on that branch instead of a scope they
  // can't touch (and can't switch back to, since the option isn't rendered).
  useEffect(() => {
    if (!canWrite && scope === TENANT_SCOPE && selectableBranches.length > 0) {
      // oxlint-disable-next-line set-state-in-effect
      setScope(selectableBranches[0].id)
    }
  }, [canWrite, scope, selectableBranches])

  // ---- Receipt settings form state (14 fields) ----
  const [values, setValues] = useState<UpdateReceiptSettingsRequest | null>(null)
  const [fieldErr, setFieldErr] = useState<Record<string, string>>({})
  const [confirmReset, setConfirmReset] = useState(false)

  useEffect(() => {
    if (!receiptSettings.data) return
    // oxlint-disable-next-line set-state-in-effect
    setValues(toFormValues(receiptSettings.data))
    setFieldErr({})
  }, [receiptSettings.data, selectedBranchId])

  const baseline = receiptSettings.data ? toFormValues(receiptSettings.data) : null
  const dirty = !!values && !!baseline && JSON.stringify(values) !== JSON.stringify(baseline)

  const set = <K extends keyof UpdateReceiptSettingsRequest>(
    key: K,
    value: UpdateReceiptSettingsRequest[K],
  ) => setValues((prev) => (prev ? { ...prev, [key]: value } : prev))

  const mutation = useMutation({
    mutationFn: () => {
      if (!values) throw new Error('No values to save.')
      return receiptSettingsApi.update(selectedBranchId, values)
    },
    onSuccess: (updated) => {
      qc.setQueryData(RECEIPT_SETTINGS_QUERY_KEY(selectedBranchId), updated)
      qc.invalidateQueries({ queryKey: RECEIPT_SETTINGS_QUERY_KEY(selectedBranchId) })
      toast('success', 'Receipt settings saved')
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 403) {
        toast('error', "You don't have permission to change receipt settings.")
        return
      }
      const fields = fieldErrorsFrom(err)
      if (Object.keys(fields).length > 0) {
        setFieldErr(fields)
        toast('error', 'Please fix the highlighted fields.')
        return
      }
      toast('error', err instanceof Error ? err.message : 'Could not save receipt settings.')
    },
  })

  const resetMutation = useMutation({
    mutationFn: () => {
      if (!selectedBranchId) throw new Error('Pick a branch first.')
      return receiptSettingsApi.reset(selectedBranchId)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: RECEIPT_SETTINGS_QUERY_KEY(selectedBranchId) })
      toast('success', 'Branch reverted to tenant defaults')
      setConfirmReset(false)
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 403) {
        toast('error', "You don't have permission to change receipt settings.")
        return
      }
      toast('error', err instanceof Error ? err.message : 'Could not reset receipt settings.')
    },
  })

  // ---- Business info form state ----
  const [bizContact, setBizContact] = useState('')
  const [bizTaxId, setBizTaxId] = useState('')

  useEffect(() => {
    if (!businessInfo.data) return
    // oxlint-disable-next-line set-state-in-effect
    setBizContact(businessInfo.data.contactNumber ?? '')
    setBizTaxId(businessInfo.data.taxId ?? '')
  }, [businessInfo.data])

  const bizDirty =
    !!businessInfo.data &&
    (bizContact !== (businessInfo.data.contactNumber ?? '') ||
      bizTaxId !== (businessInfo.data.taxId ?? ''))

  const bizMutation = useMutation({
    mutationFn: () =>
      settingsApi.updateBusinessInfo({
        contactNumber: bizContact.trim() === '' ? null : bizContact.trim(),
        taxId: bizTaxId.trim() === '' ? null : bizTaxId.trim(),
      }),
    onSuccess: (updated) => {
      qc.setQueryData(['settings', 'business-info'], updated)
      qc.invalidateQueries({ queryKey: ['settings', 'business-info'] })
      toast('success', 'Business info saved')
    },
    onError: (err) => {
      if (err instanceof ApiError && err.status === 403) {
        toast('error', 'You do not have permission to change business information.')
        return
      }
      toast('error', err instanceof Error ? err.message : 'Could not save business info.')
    },
  })

  // ---- Loading / error ----
  if (receiptSettings.isPending || !values) {
    return (
      <Card>
        <LoadingState label="Loading receipt settings…" />
      </Card>
    )
  }
  if (receiptSettings.isError) {
    return (
      <ErrorState
        title="Could not load receipt settings"
        message={
          receiptSettings.error instanceof Error
            ? receiptSettings.error.message
            : 'Please try again.'
        }
        onRetry={() => receiptSettings.refetch()}
      />
    )
  }

  const dto = receiptSettings.data!
  const selectedBranch = branches.find((b) => b.id === selectedBranchId)
  // Mirror the backend's BusinessAddress / BusinessContactNumber composition (ReceiptService) so the
  // preview can't visually diverge from the real print. For the tenant-default scope (no single
  // branch) use the first branch as a representative.
  const addressSource = selectedBranch ?? branches[0]
  const composedAddress = addressSource
    ? [addressSource.addressLine1, addressSource.city, addressSource.province]
        .map((p) => p?.trim())
        .filter((p): p is string => !!p)
        .join(', ') || null
    : null
  const businessValues = {
    businessName: businessInfo.data?.businessName ?? '',
    branchName: selectedBranch?.name ?? '',
    address: composedAddress,
    contactNumber: selectedBranch?.contactNumber ?? businessInfo.data?.contactNumber ?? null,
    taxId: businessInfo.data?.taxId ?? null,
  }

  const showReset = selectedBranchId != null && dto.isOverride === true
  const updatedLine =
    dto.updatedByName && dto.updatedAtUtc
      ? `Last updated by ${dto.updatedByName} · ${new Date(dto.updatedAtUtc).toLocaleString()}`
      : dto.updatedByName
        ? `Last updated by ${dto.updatedByName}`
        : null

  const subTabs: { id: SubTab; label: string }[] = [
    { id: 'general', label: 'General' },
    { id: 'sales', label: 'Sales receipt' },
    { id: 'delivery', label: 'Delivery receipt' },
  ]

  return (
    <div className="space-y-6">
      {/* Scope selector */}
      <Card padding="lg" className="space-y-4">
        <div className="max-w-xs">
          <Select
            label="Applies to"
            name="scope"
            value={scope}
            onChange={(e) => setScope(e.target.value)}
            hint="Branch overrides fall back to the tenant default for anything left unchanged."
          >
            {canWrite && <option value={TENANT_SCOPE}>Tenant default</option>}
            {selectableBranches.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name}
              </option>
            ))}
          </Select>
        </div>
        {updatedLine && <p className="text-[13px] text-text-muted">{updatedLine}</p>}
        {!canEditReceipts && (
          <Callout tone="info">
            These settings are read-only for your role or scope. Ask an owner or admin to change
            them.
          </Callout>
        )}
      </Card>

      {/* Sub-tabs */}
      <div className="border-b border-border">
        <nav className="-mb-px flex gap-6" aria-label="Receipt settings sections">
          {subTabs.map((t) => (
            <button
              key={t.id}
              type="button"
              onClick={() => setTab(t.id)}
              className={cn(
                'border-b-2 px-1 pb-3 text-sm font-semibold transition-colors',
                tab === t.id
                  ? 'border-primary-500 text-text-primary'
                  : 'border-transparent text-text-muted hover:text-text-primary',
              )}
            >
              {t.label}
            </button>
          ))}
        </nav>
      </div>

      {tab === 'general' && (
        <div className="space-y-6">
          <Card padding="lg" className="max-w-xl space-y-5">
            <div>
              <h2 className="text-base font-bold text-text-primary">Business information</h2>
              <p className="mt-1 text-[13px] text-text-muted">
                Shown at the top of every receipt unless a custom header is set.
              </p>
            </div>

            {!canWrite && (
              <Callout tone="info">
                Business information is read-only for your role. Ask an owner or admin to change it.
              </Callout>
            )}

            <div>
              <span className="block text-sm font-semibold text-text-primary">Business name</span>
              <p className="mt-1 text-sm text-text-secondary">
                {businessInfo.data?.businessName || '—'}
              </p>
              <p className="mt-0.5 text-[13px] text-text-muted">Edit in Settings.</p>
            </div>

            <TextField
              label="Contact number"
              name="contactNumber"
              value={bizContact}
              onChange={(e) => setBizContact(e.target.value)}
              disabled={!canWrite}
            />
            <TextField
              label="Tax / TIN"
              name="taxId"
              value={bizTaxId}
              onChange={(e) => setBizTaxId(e.target.value)}
              disabled={!canWrite}
            />

            {canWrite && (
              <div className="flex items-center gap-3 pt-1">
                <Button
                  size="sm"
                  onClick={() => bizMutation.mutate()}
                  loading={bizMutation.isPending}
                  disabled={!bizDirty}
                >
                  Save business info
                </Button>
                {bizDirty && !bizMutation.isPending && (
                  <button
                    type="button"
                    onClick={() => {
                      setBizContact(businessInfo.data?.contactNumber ?? '')
                      setBizTaxId(businessInfo.data?.taxId ?? '')
                    }}
                    className="text-[13px] font-semibold text-text-secondary hover:text-text-primary"
                  >
                    Discard
                  </button>
                )}
              </div>
            )}
          </Card>

          <Card padding="lg" className="max-w-xl space-y-4">
            <div>
              <h2 className="text-base font-bold text-text-primary">Receipt width</h2>
              <p className="mt-1 text-[13px] text-text-muted">
                Match your thermal printer's paper roll.
              </p>
            </div>
            <div className="flex flex-col gap-3">
              {(
                [
                  { v: 'Mm80', label: '80 mm' },
                  { v: 'Mm58', label: '58 mm (compact)' },
                ] as const
              ).map((opt) => (
                <label key={opt.v} className="flex items-center gap-2.5">
                  <input
                    type="radio"
                    name="width"
                    checked={values.width === opt.v}
                    onChange={() => set('width', opt.v)}
                    disabled={!canEditReceipts}
                    className="size-4 border-border-strong text-primary-600 focus:ring-primary-500/30 disabled:cursor-not-allowed"
                  />
                  <span className="text-sm text-text-secondary">{opt.label}</span>
                </label>
              ))}
            </div>
          </Card>
        </div>
      )}

      {tab === 'sales' && (
        <div className="grid gap-6 lg:grid-cols-2">
          <Card padding="lg" className="space-y-5">
            <TextArea
              label="Header"
              name="salesHeaderText"
              value={values.salesHeaderText ?? ''}
              onChange={(e) => set('salesHeaderText', e.target.value === '' ? null : e.target.value)}
              error={fieldErr.salesheadertext}
              hint="Overrides the business info block at the top. One line per row."
              disabled={!canEditReceipts}
            />
            <fieldset className="space-y-3">
              <legend className="text-sm font-semibold text-text-primary">Show on receipt</legend>
              <Check
                label="Branch name"
                checked={values.salesShowBranch}
                onChange={(v) => set('salesShowBranch', v)}
                disabled={!canEditReceipts}
              />
              <Check
                label="Cashier"
                checked={values.salesShowCashier}
                onChange={(v) => set('salesShowCashier', v)}
                disabled={!canEditReceipts}
              />
              <Check
                label="Payment method"
                checked={values.salesShowPaymentMethod}
                onChange={(v) => set('salesShowPaymentMethod', v)}
                disabled={!canEditReceipts}
              />
              <Check
                label="Tax line"
                checked={values.salesShowTaxLine}
                onChange={(v) => set('salesShowTaxLine', v)}
                disabled={!canEditReceipts}
              />
              <Check
                label="Payment reference number"
                checked={values.salesShowReferenceNumber}
                onChange={(v) => set('salesShowReferenceNumber', v)}
                disabled={!canEditReceipts}
              />
            </fieldset>
            <TextArea
              label="Footer"
              name="salesFooterText"
              value={values.salesFooterText ?? ''}
              onChange={(e) => set('salesFooterText', e.target.value === '' ? null : e.target.value)}
              error={fieldErr.salesfootertext}
              hint="Printed at the bottom, e.g. a return policy or thank-you note."
              disabled={!canEditReceipts}
            />
          </Card>
          <ReceiptPreview kind="sales" values={values} business={businessValues} />
        </div>
      )}

      {tab === 'delivery' && (
        <div className="grid gap-6 lg:grid-cols-2">
          <Card padding="lg" className="space-y-5">
            <TextArea
              label="Header"
              name="deliveryHeaderText"
              value={values.deliveryHeaderText ?? ''}
              onChange={(e) =>
                set('deliveryHeaderText', e.target.value === '' ? null : e.target.value)
              }
              error={fieldErr.deliveryheadertext}
              hint="Used as the document title, e.g. “DELIVERY RECEIPT”."
              disabled={!canEditReceipts}
            />
            <fieldset className="space-y-3">
              <legend className="text-sm font-semibold text-text-primary">Show on receipt</legend>
              <Check
                label="Prices"
                checked={values.deliveryShowPrices}
                onChange={(v) => set('deliveryShowPrices', v)}
                disabled={!canEditReceipts}
              />
              <Check
                label="Related sale number"
                checked={values.deliveryShowRelatedSaleNumber}
                onChange={(v) => set('deliveryShowRelatedSaleNumber', v)}
                disabled={!canEditReceipts}
              />
              <Check
                label="Contact number"
                checked={values.deliveryShowContactNumber}
                onChange={(v) => set('deliveryShowContactNumber', v)}
                disabled={!canEditReceipts}
              />
              <Check
                label="Signature fields"
                checked={values.deliveryShowSignatureFields}
                onChange={(v) => set('deliveryShowSignatureFields', v)}
                disabled={!canEditReceipts}
              />
            </fieldset>
            <TextArea
              label="Footer"
              name="deliveryFooterText"
              value={values.deliveryFooterText ?? ''}
              onChange={(e) =>
                set('deliveryFooterText', e.target.value === '' ? null : e.target.value)
              }
              error={fieldErr.deliveryfootertext}
              disabled={!canEditReceipts}
            />
          </Card>
          <ReceiptPreview kind="delivery" values={values} business={businessValues} />
        </div>
      )}

      {/* Save / Discard / Reset */}
      {canEditReceipts && (
        <div className="flex flex-wrap items-center gap-3">
          <Button
            size="sm"
            onClick={() => mutation.mutate()}
            loading={mutation.isPending}
            disabled={!dirty}
          >
            Save changes
          </Button>
          {dirty && !mutation.isPending && (
            <button
              type="button"
              onClick={() => {
                setValues(toFormValues(dto))
                setFieldErr({})
              }}
              className="text-[13px] font-semibold text-text-secondary hover:text-text-primary"
            >
              Discard
            </button>
          )}
          {showReset && (
            <Button
              variant="secondary"
              size="sm"
              className="ml-auto"
              onClick={() => setConfirmReset(true)}
            >
              Reset to tenant defaults
            </Button>
          )}
        </div>
      )}

      <ConfirmDialog
        open={confirmReset}
        onClose={() => setConfirmReset(false)}
        onConfirm={() => resetMutation.mutate()}
        title="Reset to tenant defaults?"
        message="This branch will drop its receipt overrides and use the tenant-wide settings. This can't be undone."
        confirmLabel="Reset"
        tone="danger"
        loading={resetMutation.isPending}
      />
    </div>
  )
}

function Check({
  label,
  checked,
  onChange,
  disabled,
}: {
  label: string
  checked: boolean
  onChange: (v: boolean) => void
  disabled?: boolean
}) {
  return (
    <label className="flex items-center gap-3">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        disabled={disabled}
        className="size-4 rounded border-border-strong text-primary-600 focus:ring-primary-500/30 disabled:cursor-not-allowed"
      />
      <span className="text-sm text-text-secondary">{label}</span>
    </label>
  )
}
