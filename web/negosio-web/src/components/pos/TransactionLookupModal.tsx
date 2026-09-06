import { useEffect, useState } from 'react'
import { ApiError } from '../../api/client'
import { salesApi } from '../../api/pos'
import type { SaleDetailDto, SaleSummaryDto } from '../../api/types'
import { cn } from '../../lib/cn'
import { formatMoney } from '../../lib/format'
import { StatusBadge } from '../sales/StatusBadge'
import { Button, Callout, Modal } from '../ui'
import { inputClass } from '../ui/TextField'

interface Eligibility {
  ok: boolean
  message?: string
}

interface Props {
  open: boolean
  onClose: () => void
  title: string
  helperText?: string
  actionLabel: string
  isEligible: (sale: SaleDetailDto) => Eligibility
  onContinue: (sale: SaleDetailDto) => void
  /** Prefills the search field with the terminal's most recent sale — a suggestion the cashier can
   * search with as-is or overwrite, never an automatic pick. */
  initialSaleNumber?: string
}

/**
 * Shared "find a sale by number" flow for Void / Returns / Reprint. Resolves a sale number to a
 * full SaleDetailDto (reusing the same list-search + get-by-id calls as the Sales pages, so
 * branch-scoping for Cashier/Manager is enforced identically) and shows a compact summary, then
 * hands off to the caller's action-specific continuation. Never mutates a Sale.
 */
export function TransactionLookupModal({
  open,
  onClose,
  title,
  helperText,
  actionLabel,
  isEligible,
  onContinue,
  initialSaleNumber,
}: Props) {
  const [term, setTerm] = useState('')
  const [searching, setSearching] = useState(false)
  const [error, setError] = useState('')
  const [candidates, setCandidates] = useState<SaleSummaryDto[] | null>(null)
  const [selected, setSelected] = useState<SaleDetailDto | null>(null)

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setTerm(initialSaleNumber ?? '')
    setSearching(false)
    setError('')
    setCandidates(null)
    setSelected(null)
  }, [open, initialSaleNumber])

  const loadDetail = async (id: string) => {
    try {
      setSelected(await salesApi.get(id))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not load that sale.')
    }
  }

  const find = async () => {
    const q = term.trim()
    if (!q) return
    setSearching(true)
    setError('')
    setCandidates(null)
    try {
      const result = await salesApi.list({ search: q, pageSize: 5 })
      if (result.items.length === 0) {
        setError(`No sale found for "${q}".`)
      } else if (result.items.length === 1) {
        await loadDetail(result.items[0].id)
      } else {
        setCandidates(result.items)
      }
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Sale lookup failed.')
    } finally {
      setSearching(false)
    }
  }

  const eligibility = selected ? isEligible(selected) : null

  return (
    <Modal open={open} onClose={onClose} title={title}>
      <div className="space-y-4">
        {helperText && <p className="text-[13px] text-text-muted">{helperText}</p>}

        {!selected && (
          <>
            <div>
              <label
                htmlFor="lookup-sale-number"
                className="mb-1.5 block text-sm font-semibold text-text-secondary"
              >
                Sale number
              </label>
              <div className="flex gap-2">
                <input
                  id="lookup-sale-number"
                  name="saleNumber"
                  value={term}
                  onChange={(e) => setTerm(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault()
                      find()
                    }
                  }}
                  autoFocus
                  placeholder="0000042"
                  className={cn(inputClass, 'flex-1')}
                />
                <Button
                  size="md"
                  className="shrink-0"
                  onClick={find}
                  loading={searching}
                  disabled={!term.trim()}
                >
                  Find sale
                </Button>
              </div>
            </div>

            {error && <Callout tone="error">{error}</Callout>}

            {candidates && candidates.length > 1 && (
              <ul className="divide-y divide-border rounded-lg border border-border">
                {candidates.map((s) => (
                  <li key={s.id}>
                    <button
                      type="button"
                      onClick={() => loadDetail(s.id)}
                      className="flex h-auto w-full items-center justify-between gap-2 px-3 py-2 text-left hover:bg-surface-subtle"
                    >
                      <span className="font-semibold text-text-primary">#{s.saleNumber}</span>
                      <span className="text-text-muted">
                        {new Date(s.createdAtUtc).toLocaleString()}
                      </span>
                      <span className="font-semibold">{formatMoney(s.grandTotal)}</span>
                      <StatusBadge status={s.status} />
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </>
        )}

        {selected && (
          <>
            <div className="rounded-xl border border-border bg-surface-subtle p-4">
              <div className="flex items-center justify-between gap-2">
                <p className="text-base font-bold text-text-primary">
                  Sale #{selected.sale.saleNumber}
                </p>
                <StatusBadge status={selected.sale.status} />
              </div>
              <dl className="mt-2 space-y-1 text-[13px] text-text-secondary">
                <div className="flex justify-between">
                  <dt>Date/time</dt>
                  <dd>{new Date(selected.sale.createdAtUtc).toLocaleString()}</dd>
                </div>
                <div className="flex justify-between">
                  <dt>Cashier</dt>
                  <dd>{selected.sale.cashierName}</dd>
                </div>
                <div className="flex justify-between">
                  <dt>Total</dt>
                  <dd className="font-semibold text-text-primary">
                    {formatMoney(selected.sale.grandTotal)}
                  </dd>
                </div>
              </dl>
            </div>

            {eligibility && !eligibility.ok && (
              <Callout tone="warning">{eligibility.message}</Callout>
            )}

            <div className="flex justify-end gap-2">
              <Button variant="secondary" size="sm" onClick={() => setSelected(null)}>
                Look up another sale
              </Button>
              {eligibility?.ok && (
                <Button size="sm" onClick={() => onContinue(selected)}>
                  {actionLabel}
                </Button>
              )}
            </div>
          </>
        )}
      </div>
    </Modal>
  )
}
