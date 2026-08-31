import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../api/client'
import { settingsApi } from '../api/settings'
import type { TaxSettingsDto } from '../api/types'
import { TAX_SETTINGS_QUERY_KEY, useTaxSettings } from '../hooks/useTaxSettings'
import { fieldErrorsFrom } from '../lib/formErrors'
import { useCan } from '../lib/useCan'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Button,
  Callout,
  Card,
  ErrorState,
  LoadingState,
  TextField,
  useToast,
} from '../components/ui'

export default function SettingsPage() {
  return (
    <DashboardLayout title="Settings">
      <div className="space-y-6">
        <h1 className="text-2xl font-bold text-text-primary">Settings</h1>
        <TaxSettingsCard />
      </div>
    </DashboardLayout>
  )
}

function TaxSettingsCard() {
  const qc = useQueryClient()
  const { toast } = useToast()
  const canWrite = useCan('settings:write')
  const { data, isPending, isError, error, refetch } = useTaxSettings()

  const [rate, setRate] = useState('')
  const [pricesIncludeTax, setPricesIncludeTax] = useState(false)
  const [rateError, setRateError] = useState('')

  // Seed / re-seed the form from the server value whenever it (re)loads.
  useEffect(() => {
    if (!data) return
    // oxlint-disable-next-line set-state-in-effect
    setRate(String(data.taxRatePercent))
    setPricesIncludeTax(data.pricesIncludeTax)
    setRateError('')
  }, [data])

  const dirty =
    !!data && (Number(rate) !== data.taxRatePercent || pricesIncludeTax !== data.pricesIncludeTax)

  const mutation = useMutation({
    mutationFn: () =>
      settingsApi.updateTax({ taxRatePercent: Number(rate), pricesIncludeTax }),
    onSuccess: (updated: TaxSettingsDto) => {
      // Same key the POS cart preview reads — the next transaction sees the new rate.
      qc.setQueryData(TAX_SETTINGS_QUERY_KEY, updated)
      qc.invalidateQueries({ queryKey: TAX_SETTINGS_QUERY_KEY })
      toast('success', 'Tax settings saved')
    },
    onError: (err) => {
      const fields = fieldErrorsFrom(err)
      if (fields.taxratepercent) {
        setRateError(fields.taxratepercent)
        return
      }
      if (err instanceof ApiError && err.status === 403) {
        toast('error', 'You do not have permission to change tax settings.')
        return
      }
      toast('error', err instanceof Error ? err.message : 'Could not save tax settings.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setRateError('')
    const value = Number(rate)
    if (rate.trim() === '' || !Number.isFinite(value) || value < 0 || value > 100) {
      setRateError('Enter a tax rate between 0 and 100.')
      return
    }
    mutation.mutate()
  }

  if (isPending) {
    return (
      <Card>
        <LoadingState label="Loading tax settings…" />
      </Card>
    )
  }
  if (isError || !data) {
    return (
      <ErrorState
        title="Could not load tax settings"
        message={error instanceof Error ? error.message : 'Please try again.'}
        onRetry={() => refetch()}
      />
    )
  }

  return (
    <Card padding="lg" className="max-w-xl space-y-5">
      <div>
        <h2 className="text-base font-bold text-text-primary">Tax</h2>
        <p className="mt-1 text-[13px] text-text-muted">
          Applied to every sale line at checkout. A rate of 0% means no tax is charged.
        </p>
      </div>

      {!canWrite && (
        <Callout tone="info">
          These settings are read-only for your role. Ask an owner or admin to change them.
        </Callout>
      )}

      <form onSubmit={submit} className="space-y-4">
        <TextField
          label="Tax rate"
          name="taxRatePercent"
          type="number"
          min={0}
          max={100}
          step="0.01"
          value={rate}
          onChange={(e) => setRate(e.target.value)}
          error={rateError || undefined}
          hint="Percentage, e.g. 12 for 12%. Between 0 and 100."
          disabled={!canWrite}
        />

        <label className="flex items-start gap-3">
          <input
            type="checkbox"
            checked={pricesIncludeTax}
            onChange={(e) => setPricesIncludeTax(e.target.checked)}
            disabled={!canWrite}
            className="mt-0.5 size-4 rounded border-border-strong text-primary-600 focus:ring-primary-500/30 disabled:cursor-not-allowed"
          />
          <span className="text-sm text-text-secondary">
            <span className="font-semibold text-text-primary">Prices include tax</span>
            <span className="mt-0.5 block text-[13px] text-text-muted">
              When on, product prices are treated as tax-inclusive and the tax shown on the
              receipt is the portion already inside the price. When off, tax is added on top.
            </span>
          </span>
        </label>

        {canWrite && (
          <div className="flex items-center gap-3 pt-1">
            <Button size="sm" onClick={submit} loading={mutation.isPending} disabled={!dirty}>
              Save changes
            </Button>
            {dirty && !mutation.isPending && (
              <button
                type="button"
                onClick={() => {
                  setRate(String(data.taxRatePercent))
                  setPricesIncludeTax(data.pricesIncludeTax)
                  setRateError('')
                }}
                className="text-[13px] font-semibold text-text-secondary hover:text-text-primary"
              >
                Discard
              </button>
            )}
          </div>
        )}
      </form>
    </Card>
  )
}
