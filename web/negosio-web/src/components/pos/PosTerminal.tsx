import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { PackageSearch } from 'lucide-react'
import { ApiError } from '../../api/client'
import { checkoutApi, posCatalogApi } from '../../api/pos'
import type {
  CheckoutPaymentInput,
  CheckoutRequest,
  DiscountType,
  PosCatalogItemDto,
  SaleDetailDto,
  SaleResultDto,
} from '../../api/types'
import { posStorage, type TerminalCtx } from '../../lib/posStorage'
import { usePosCart } from '../../hooks/usePosCart'
import { useBarcodeScanner } from '../../hooks/useBarcodeScanner'
import { useDebouncedValue } from '../../hooks/useDebouncedValue'
import { usePosShortcuts } from '../../hooks/usePosShortcuts'
import { useTaxSettings } from '../../hooks/useTaxSettings'
import { calcTotals } from '../../lib/saleMath'
import { formatMoney } from '../../lib/format'
import type { CurrentSaleRef } from '../../lib/pos'
import { VOID_INELIGIBLE_MESSAGES } from '../../lib/pos'
import { hasReturnableQty } from '../../lib/returns'
import { useCan } from '../../lib/useCan'
import { ReturnModal } from '../sales/ReturnModal'
import { VoidSaleModal } from '../sales/VoidSaleModal'
import { Callout, ConfirmDialog, EmptyState, ErrorState, Pagination, SkeletonText, useToast } from '../ui'
import { CartPanel } from './CartPanel'
import { CheckPriceModal } from './CheckPriceModal'
import { PaymentModal } from './PaymentModal'
import { PosActionBar } from './PosActionBar'
import { PosProductGrid } from './PosProductGrid'
import { PosSearchBar } from './PosSearchBar'
import { ReprintReceiptModal } from './ReprintReceiptModal'
import { TransactionDiscountModal } from './TransactionDiscountModal'
import { TransactionLookupModal } from './TransactionLookupModal'

const PAGE_SIZE = 24

// Codes that mean "the order itself is wrong" — the id must rotate after the next cart edit.
const DEFINITIVE_REJECTIONS = new Set([
  'INSUFFICIENT_INVENTORY',
  'INVALID_SALE_ITEM',
  'INVALID_QUANTITY',
  'INVALID_DISCOUNT',
])

interface Props {
  tenantId: string
  branchId: string
  registerId: string
  registerSessionId: string
  /** The terminal's current sale — a successful checkout here, or (surviving a refresh / Exit ->
   * Continue) the most recent sale in this register session, resolved by the parent. Independent
   * of the active cart: Void and Reprint act on this, never on activeCart. */
  currentSale: CurrentSaleRef | null
  onSaleCompleted: (result: SaleResultDto) => void
  onSessionLost: () => void
}

export function PosTerminal({
  tenantId,
  branchId,
  registerId,
  registerSessionId,
  currentSale,
  onSaleCompleted,
  onSessionLost,
}: Props) {
  const ctx: TerminalCtx = useMemo(
    () => ({ tenantId, branchId, registerId, registerSessionId }),
    [tenantId, branchId, registerId, registerSessionId],
  )
  const cart = usePosCart(ctx)
  const tax = useTaxSettings()
  const qc = useQueryClient()
  const { toast } = useToast()

  const [term, setTerm] = useState('')
  const [page, setPage] = useState(1)
  const debounced = useDebouncedValue(term, 300)

  const [payOpen, setPayOpen] = useState(false)
  const [status, setStatus] = useState<'idle' | 'submitting' | 'failed'>('idle')
  const [checkoutError, setCheckoutError] = useState<string | null>(null)
  const [payError, setPayError] = useState<string | null>(null)
  const [needsNewId, setNeedsNewId] = useState(false)
  const [restoredAttempt, setRestoredAttempt] = useState<string | null>(null)

  const idRef = useRef<string | null>(null)
  const searchInputRef = useRef<HTMLInputElement>(null)

  const canVoid = useCan('sales:void')
  const canReturn = useCan('refund:manage')

  const [newTxnConfirmOpen, setNewTxnConfirmOpen] = useState(false)
  const [voidLookupOpen, setVoidLookupOpen] = useState(false)
  const [voidTarget, setVoidTarget] = useState<SaleDetailDto | null>(null)
  const [returnLookupOpen, setReturnLookupOpen] = useState(false)
  const [returnTarget, setReturnTarget] = useState<SaleDetailDto | null>(null)
  const [reprintOpen, setReprintOpen] = useState(false)
  const [checkPriceOpen, setCheckPriceOpen] = useState(false)
  const [discountOpen, setDiscountOpen] = useState(false)

  // Restore an unresolved checkout-attempt id (survives refresh / crash).
  useEffect(() => {
    const existing = posStorage.readAttemptId(ctx)
    idRef.current = existing
    // oxlint-disable-next-line set-state-in-effect
    setRestoredAttempt(existing)
  }, [ctx])

  const ensureId = useCallback(() => {
    if (!idRef.current) {
      idRef.current = crypto.randomUUID()
      posStorage.writeAttemptId(ctx, idRef.current)
    }
    return idRef.current
  }, [ctx])

  // A non-empty cart always has (or regenerates) an attempt id.
  useEffect(() => {
    if (!cart.isEmpty) ensureId()
  }, [cart.isEmpty, ensureId])

  useEffect(() => {
    // oxlint-disable-next-line set-state-in-effect
    setPage(1)
  }, [debounced])

  const catalog = useQuery({
    queryKey: ['pos-catalog', { branchId, search: debounced, page }],
    queryFn: () =>
      posCatalogApi.search({ branchId, search: debounced || undefined, page, pageSize: PAGE_SIZE }),
  })

  const rotateIfNeeded = useCallback(() => {
    if (needsNewId) {
      idRef.current = null
      posStorage.clearAttemptId(ctx)
      setNeedsNewId(false)
      setRestoredAttempt(null)
    }
  }, [needsNewId, ctx])

  const addItem = cart.addItem
  const setQty = cart.setQty
  const removeLine = cart.removeLine
  const setLineDiscount = cart.setLineDiscount

  const onAdd = useCallback(
    (item: Parameters<typeof addItem>[0]) => {
      rotateIfNeeded()
      addItem(item)
    },
    [rotateIfNeeded, addItem],
  )
  const onSetQty = useCallback(
    (variantId: string, qty: number) => {
      rotateIfNeeded()
      setQty(variantId, qty)
    },
    [rotateIfNeeded, setQty],
  )
  const onRemove = useCallback(
    (variantId: string) => {
      rotateIfNeeded()
      removeLine(variantId)
    },
    [rotateIfNeeded, removeLine],
  )
  const onSetDiscount = useCallback(
    (variantId: string, d: { type: DiscountType; value: number }) => {
      rotateIfNeeded()
      setLineDiscount(variantId, d)
    },
    [rotateIfNeeded, setLineDiscount],
  )

  // New Transaction: an empty cart has nothing to clear — just return focus to search. A
  // non-empty cart is confirmed first (see ConfirmDialog below), then cleared the same way a
  // successful checkout clears it: cart.clear() drops the persisted cart AND attempt id, and we
  // additionally null the in-memory idRef (mirroring rotateIfNeeded) so the next add regenerates
  // a fresh clientRequestId rather than reusing a stale one. Register/branch/session are untouched.
  const onNewTransaction = useCallback(() => {
    if (cart.isEmpty) {
      searchInputRef.current?.focus()
      return
    }
    setNewTxnConfirmOpen(true)
  }, [cart.isEmpty])

  const confirmNewTransaction = useCallback(() => {
    idRef.current = null
    cart.clear()
    setPayOpen(false)
    setStatus('idle')
    setCheckoutError(null)
    setPayError(null)
    setNeedsNewId(false)
    setRestoredAttempt(null)
    setNewTxnConfirmOpen(false)
  }, [cart])

  // Eligibility for the two sale-lookup flows. Both only decide what the lookup modal can show a
  // "continue" button for — the backend independently re-checks and is authoritative either way
  // (VoidSaleModal / ReturnModal re-validate on submit).
  const voidEligibility = useCallback(
    (sale: SaleDetailDto) => {
      if (sale.sale.status !== 'Completed') {
        return {
          ok: false,
          message: `This sale is already ${sale.sale.status.toLowerCase()} and cannot be voided.`,
        }
      }
      if (!sale.canVoid) {
        return {
          ok: false,
          message: sale.voidIneligibilityCode
            ? (VOID_INELIGIBLE_MESSAGES[sale.voidIneligibilityCode] ?? 'This sale cannot be voided.')
            : 'This sale cannot be voided.',
        }
      }
      return { ok: true }
    },
    [],
  )

  const returnEligibility = useCallback((sale: SaleDetailDto) => {
    if (sale.sale.status !== 'Completed' && sale.sale.status !== 'PartiallyRefunded') {
      return {
        ok: false,
        message: `This sale is ${sale.sale.status.toLowerCase()} and has nothing left to return.`,
      }
    }
    if (!hasReturnableQty(sale.items)) {
      return { ok: false, message: 'Every item on this sale has already been fully returned.' }
    }
    return { ok: true }
  }, [])

  // Reprint skips the manual sale-number lookup when the terminal already knows the current sale;
  // it falls back to the existing lookup flow only when there isn't one at all (a genuinely empty
  // session — no sale has ever completed here yet).
  const startReprint = useCallback(() => {
    if (currentSale) {
      window.open(`/sales/${currentSale.saleId}/receipt?print=1`, '_blank', 'noopener')
      return
    }
    setReprintOpen(true)
  }, [currentSale])

  const anyPosModalOpen =
    newTxnConfirmOpen ||
    voidLookupOpen ||
    voidTarget != null ||
    returnLookupOpen ||
    returnTarget != null ||
    reprintOpen ||
    checkPriceOpen ||
    discountOpen ||
    payOpen

  usePosShortcuts(
    {
      onNewTransaction,
      onCheckPrice: () => setCheckPriceOpen(true),
      onDiscounts: () => !cart.isEmpty && setDiscountOpen(true),
      onReturns: () => canReturn && setReturnLookupOpen(true),
      onReprint: startReprint,
    },
    !anyPosModalOpen,
  )

  async function lookupBarcode(code: string) {
    if (!code) return
    try {
      const item = await posCatalogApi.barcode(code, branchId)
      onAdd(item)
      toast('success', `Added ${item.productName}`)
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) toast('info', `No product for barcode ${code}`)
      else toast('error', e instanceof Error ? e.message : 'Lookup failed')
    }
  }

  useBarcodeScanner({ enabled: true, onScan: lookupBarcode })

  const totals =
    tax.data != null
      ? calcTotals(
          cart.lines.map((l) => ({
            unitPrice: l.unitPrice,
            quantity: l.quantity,
            discount: l.discount,
          })),
          tax.data,
        )
      : { subtotal: 0, discountTotal: 0, taxTotal: 0, grandTotal: 0 }

  const mutation = useMutation({
    mutationFn: (payment: CheckoutPaymentInput) => {
      const clientRequestId = ensureId()
      const body: CheckoutRequest = {
        branchId,
        registerSessionId: ctx.registerSessionId,
        clientRequestId,
        items: cart.lines.map((l) => ({
          productVariantId: l.variantId,
          quantity: l.quantity,
          discount: l.discount.type === 'None' ? null : l.discount,
        })),
        payments: [payment],
      }
      return checkoutApi.checkout(body)
    },
    onMutate: () => {
      setStatus('submitting')
      setPayError(null)
      setCheckoutError(null)
    },
    onSuccess: (result) => {
      idRef.current = null
      setStatus('idle')
      setPayOpen(false)
      setRestoredAttempt(null)
      cart.clear() // also clears the persisted cart + attempt-id keys
      qc.invalidateQueries({ queryKey: ['inventory'] })
      qc.invalidateQueries({ queryKey: ['sales'] })
      qc.invalidateQueries({ queryKey: ['dashboard'] })
      onSaleCompleted(result)
      toast('success', `Sale #${result.saleNumber} completed — ${formatMoney(result.grandTotal)}`)
    },
    onError: (err) => {
      setStatus('failed')
      if (!(err instanceof ApiError)) {
        setPayError('Payment failed. Please try again.')
        return
      }
      if (err.code === 'NETWORK_ERROR') {
        setPayError(
          "We couldn't confirm the sale. Check your last sale under Sales, then Retry — it won't charge twice.",
        )
        return
      }
      if (err.code === 'CHECKOUT_CONCURRENCY_CONFLICT') {
        setPayError('That didn’t go through. Please retry.')
        return
      }
      if (err.code === 'INSUFFICIENT_INVENTORY') {
        setPayOpen(false)
        setCheckoutError(
          'Not enough stock for one or more items. Stock levels have been refreshed — adjust quantities and charge again.',
        )
        setNeedsNewId(true)
        catalog.refetch()
        return
      }
      if (err.code === 'PAYMENT_INSUFFICIENT' || err.code === 'INVALID_PAYMENT') {
        setPayError(err.message)
        return
      }
      if (DEFINITIVE_REJECTIONS.has(err.code)) {
        setPayOpen(false)
        setCheckoutError(`${err.message} Review the flagged item and try again.`)
        setNeedsNewId(true)
        return
      }
      if (err.code === 'REGISTER_SESSION_NOT_FOUND' || err.code === 'REGISTER_SESSION_NOT_OPEN') {
        setPayOpen(false)
        onSessionLost()
        return
      }
      setPayError(err.message)
    },
  })

  const resuming = restoredAttempt != null && !cart.isEmpty && status === 'idle'

  return (
    <div className="flex h-full min-h-0 flex-col">
      <PosActionBar
        onNewTransaction={onNewTransaction}
        onVoid={() => setVoidLookupOpen(true)}
        onDiscounts={() => setDiscountOpen(true)}
        onReturns={() => setReturnLookupOpen(true)}
        onReprint={startReprint}
        onCheckPrice={() => setCheckPriceOpen(true)}
        canVoid={canVoid}
        canReturn={canReturn}
        discountsDisabled={cart.isEmpty}
      />

      <div className="grid min-h-0 flex-1 grid-rows-[minmax(0,1fr)_auto] lg:grid-cols-[62%_38%] lg:grid-rows-1">
        <section className="flex min-h-0 flex-col gap-3 p-4">
          <PosSearchBar ref={searchInputRef} value={term} onChange={setTerm} onEnter={lookupBarcode} />
          <div className="min-h-0 flex-1 overflow-y-auto">
            {catalog.isError ? (
              <ErrorState
                message={(catalog.error as Error).message}
                onRetry={() => catalog.refetch()}
              />
            ) : catalog.isPending ? (
              <div className="grid grid-cols-2 gap-2 md:grid-cols-3 xl:grid-cols-4">
                {Array.from({ length: 8 }).map((_, i) => (
                  <SkeletonText key={i} className="h-28 rounded-xl" />
                ))}
              </div>
            ) : catalog.data.items.length === 0 ? (
              <EmptyState
                icon={PackageSearch}
                title="No products match"
                description="Try a different search, or check the catalog."
              />
            ) : (
              <PosProductGrid items={catalog.data.items} onAdd={onAdd} />
            )}
          </div>
          {catalog.data && (
            <Pagination
              page={catalog.data.page}
              pageSize={catalog.data.pageSize}
              totalCount={catalog.data.totalCount}
              totalPages={catalog.data.totalPages}
              onPageChange={setPage}
            />
          )}
        </section>

        <aside className="min-h-0">
          <CartPanel
            lines={cart.lines}
            totals={totals}
            tax={tax.data}
            taxPending={tax.isPending}
            onSetQty={onSetQty}
            onRemove={onRemove}
            onSetDiscount={onSetDiscount}
            onCharge={() => {
              setCheckoutError(null)
              setPayError(null)
              setStatus('idle')
              setPayOpen(true)
            }}
            chargeDisabled={status === 'submitting' || tax.isPending}
            notice={checkoutError ? <Callout tone="warning">{checkoutError}</Callout> : undefined}
            resuming={resuming}
          />
        </aside>
      </div>

      <PaymentModal
        open={payOpen}
        onClose={() => {
          if (status !== 'submitting') setPayOpen(false)
        }}
        amountDue={totals.grandTotal}
        submitting={status === 'submitting'}
        error={payError}
        onConfirm={(payment) => mutation.mutate(payment)}
      />

      <ConfirmDialog
        open={newTxnConfirmOpen}
        onClose={() => setNewTxnConfirmOpen(false)}
        onConfirm={confirmNewTransaction}
        title="Start a new transaction?"
        message={
          <>
            The current cart contains {cart.lines.length} item{cart.lines.length === 1 ? '' : 's'}.
            Starting a new transaction will clear the current cart.
          </>
        }
        confirmLabel="Start new transaction"
      />

      <TransactionLookupModal
        open={voidLookupOpen}
        onClose={() => setVoidLookupOpen(false)}
        title="Void sale"
        actionLabel="Continue to void"
        quickPickLabel="Void this sale"
        quickPick={currentSale}
        isEligible={voidEligibility}
        onContinue={(sale) => {
          setVoidLookupOpen(false)
          setVoidTarget(sale)
        }}
      />
      {voidTarget && (
        <VoidSaleModal
          open
          onClose={() => setVoidTarget(null)}
          sale={voidTarget}
          onVoided={() => {
            setVoidTarget(null)
            qc.invalidateQueries({ queryKey: ['pos-catalog'] })
          }}
        />
      )}

      <TransactionLookupModal
        open={returnLookupOpen}
        onClose={() => setReturnLookupOpen(false)}
        title="Return sale"
        actionLabel="Continue to return"
        isEligible={returnEligibility}
        onContinue={(sale) => {
          setReturnLookupOpen(false)
          setReturnTarget(sale)
        }}
      />
      {returnTarget && (
        <ReturnModal open onClose={() => setReturnTarget(null)} sale={returnTarget} />
      )}

      <ReprintReceiptModal open={reprintOpen} onClose={() => setReprintOpen(false)} />

      <CheckPriceModal
        open={checkPriceOpen}
        onClose={() => setCheckPriceOpen(false)}
        branchId={branchId}
        onAddToCart={(item: PosCatalogItemDto) => onAdd(item)}
      />

      <TransactionDiscountModal
        open={discountOpen}
        onClose={() => setDiscountOpen(false)}
        lines={cart.lines}
        onApply={(percent) => {
          rotateIfNeeded()
          for (const line of cart.lines) {
            setLineDiscount(line.variantId, { type: 'Percentage', value: percent })
          }
        }}
      />
    </div>
  )
}
