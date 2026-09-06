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
  VoidSaleApprovalInput,
} from '../../api/types'
import { posStorage, type TerminalCtx } from '../../lib/posStorage'
import { usePosCart } from '../../hooks/usePosCart'
import { useBarcodeScanner } from '../../hooks/useBarcodeScanner'
import { useDebouncedValue } from '../../hooks/useDebouncedValue'
import { usePosShortcuts } from '../../hooks/usePosShortcuts'
import { useTaxSettings } from '../../hooks/useTaxSettings'
import { calcTotals } from '../../lib/saleMath'
import type { LastSaleRef } from '../../lib/pos'
import { VOID_INELIGIBLE_MESSAGES } from '../../lib/pos'
import { hasReturnableQty } from '../../lib/returns'
import { useCan } from '../../lib/useCan'
import { ReturnModal } from '../sales/ReturnModal'
import { VoidSaleModal } from '../sales/VoidSaleModal'
import { Callout, ConfirmDialog, EmptyState, ErrorState, Pagination, SkeletonText, useToast } from '../ui'
import { CancelTransactionModal } from './CancelTransactionModal'
import { CartPanel } from './CartPanel'
import { CategoryFilters } from './CategoryFilters'
import { CheckPriceModal } from './CheckPriceModal'
import { DiscountApprovalModal } from './DiscountApprovalModal'
import { PaymentFailedModal } from './PaymentFailedModal'
import { PaymentModal } from './PaymentModal'
import { PaymentSuccessModal } from './PaymentSuccessModal'
import { PosActionBar } from './PosActionBar'
import { PosProductGrid } from './PosProductGrid'
import { PosSearchBar } from './PosSearchBar'
import { ReprintReceiptModal } from './ReprintReceiptModal'
import { TransactionDiscountModal } from './TransactionDiscountModal'
import { TransactionLookupModal } from './TransactionLookupModal'
import { VoidChoiceModal } from './VoidChoiceModal'

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
  /** The most recent completed sale in this register session, resolved by the parent straight from
   * the backend. Independent of the active cart: Reprint acts on this, never on the cart. Void
   * treats it as a search-by-number fallback only — it never lets the cashier vote "the cart" via
   * this ref, since the cart's cancel path (below) doesn't touch a Sale at all. */
  lastSale: LastSaleRef | null
  onSessionLost: () => void
}

export function PosTerminal({
  tenantId,
  branchId,
  registerId,
  registerSessionId,
  lastSale,
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
  const [categoryId, setCategoryId] = useState<string | null>(null)
  const [page, setPage] = useState(1)
  const debounced = useDebouncedValue(term, 300)

  const [payOpen, setPayOpen] = useState(false)
  const [status, setStatus] = useState<'idle' | 'submitting' | 'failed'>('idle')
  const [checkoutError, setCheckoutError] = useState<string | null>(null)
  const [payError, setPayError] = useState<string | null>(null)
  const [needsNewId, setNeedsNewId] = useState(false)
  const [restoredAttempt, setRestoredAttempt] = useState<string | null>(null)
  const [discountApprovalOpen, setDiscountApprovalOpen] = useState(false)
  const [discountApprovalError, setDiscountApprovalError] = useState<string | null>(null)
  const [successResult, setSuccessResult] = useState<SaleResultDto | null>(null)
  const [successPayment, setSuccessPayment] = useState<CheckoutPaymentInput | null>(null)
  // Only for a genuine payment-attempt failure (network/connectivity, a concurrency conflict, or a
  // truly unexpected error) — insufficient inventory, discount approval, and a lost register
  // session each already have their own targeted recovery flow and never populate this.
  const [paymentFailure, setPaymentFailure] = useState<
    { message: string; certainNotCharged: boolean } | null
  >(null)

  const idRef = useRef<string | null>(null)
  const searchInputRef = useRef<HTMLInputElement>(null)
  // The payment the cashier already confirmed — kept so a DISCOUNT_APPROVAL_REQUIRED retry can
  // resubmit the exact same charge with the approval attached, without re-prompting for payment.
  const pendingPaymentRef = useRef<CheckoutPaymentInput | null>(null)

  const canVoid = useCan('sales:void')
  const canReturn = useCan('sales:return')

  const [newTxnConfirmOpen, setNewTxnConfirmOpen] = useState(false)
  const [voidChoiceOpen, setVoidChoiceOpen] = useState(false)
  const [cancelTxnOpen, setCancelTxnOpen] = useState(false)
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
  }, [debounced, categoryId])

  const catalog = useQuery({
    queryKey: ['pos-catalog', { branchId, search: debounced, categoryId, page }],
    queryFn: () =>
      posCatalogApi.search({
        branchId,
        search: debounced || undefined,
        categoryId: categoryId ?? undefined,
        page,
        pageSize: PAGE_SIZE,
      }),
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

  // Always goes through the lookup — the terminal's last sale is only ever a suggestion (prefilled
  // into the search field below), never an automatic pick, so the cashier can reprint any sale.
  const startReprint = useCallback(() => {
    setReprintOpen(true)
  }, [])

  // Void either cancels the uncompleted cart (nothing to search — there's no Sale yet) or looks up
  // an existing sale by number. A cart with nothing in it has nothing to cancel, so skip straight
  // to the lookup in that case rather than offering a choice with only one live option.
  const onVoid = useCallback(() => {
    if (cart.isEmpty) {
      setVoidLookupOpen(true)
      return
    }
    setVoidChoiceOpen(true)
  }, [cart.isEmpty])

  const anyPosModalOpen =
    newTxnConfirmOpen ||
    voidChoiceOpen ||
    cancelTxnOpen ||
    voidLookupOpen ||
    voidTarget != null ||
    returnLookupOpen ||
    returnTarget != null ||
    reprintOpen ||
    checkPriceOpen ||
    discountOpen ||
    payOpen ||
    discountApprovalOpen ||
    successResult != null ||
    paymentFailure != null

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
    mutationFn: ({
      payment,
      approval,
    }: {
      payment: CheckoutPaymentInput
      approval?: VoidSaleApprovalInput
    }) => {
      pendingPaymentRef.current = payment
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
        approval,
      }
      return checkoutApi.checkout(body)
    },
    onMutate: () => {
      setStatus('submitting')
      setPayError(null)
      setCheckoutError(null)
    },
    onSuccess: (result, variables) => {
      idRef.current = null
      pendingPaymentRef.current = null
      setStatus('idle')
      setPayOpen(false)
      setDiscountApprovalOpen(false)
      setRestoredAttempt(null)
      cart.clear() // also clears the persisted cart + attempt-id keys
      qc.invalidateQueries({ queryKey: ['inventory'] })
      qc.invalidateQueries({ queryKey: ['sales'] })
      qc.invalidateQueries({ queryKey: ['dashboard'] })
      // The success modal is the primary confirmation; this toast is just a lightweight echo, so
      // it stays short rather than repeating everything the modal already shows in detail.
      toast('success', `Sale #${result.saleNumber} completed`)
      setSuccessPayment(variables.payment)
      setSuccessResult(result)
    },
    onError: (err) => {
      setStatus('failed')
      if (!(err instanceof ApiError)) {
        // Truly unexpected (not even a typed API error) — no way to know whether the sale was
        // actually recorded, so this is deliberately the ambiguous message, never a false "not
        // charged" guarantee.
        setPayOpen(false)
        setPaymentFailure({
          message: "We couldn't complete the payment. Please try again.",
          certainNotCharged: false,
        })
        return
      }
      if (err.code === 'DISCOUNT_APPROVAL_REQUIRED') {
        // First attempt from a Cashier without the grant: reveal the approval dialog, keeping the
        // already-confirmed payment (pendingPaymentRef) so the retry doesn't re-prompt for it.
        setPayOpen(false)
        setDiscountApprovalError(null)
        setDiscountApprovalOpen(true)
        return
      }
      if (err.code === 'INVALID_APPROVER_CREDENTIALS') {
        // Only ever reached via a discount-approval retry — Void/CashDrawer have their own modals
        // and their own onError handlers, never this mutation.
        setDiscountApprovalError('Invalid manager credentials.')
        return
      }
      if (err.code === 'VOID_APPROVER_WRONG_BRANCH') {
        setDiscountApprovalError('This manager cannot approve this for your branch.')
        return
      }
      if (err.code === 'NETWORK_ERROR') {
        // The response was lost, not necessarily the request — the sale may have completed on the
        // backend even though this client never saw it. Never claim "not charged" here; a retry is
        // still safe (same clientRequestId, so the backend just returns the existing sale instead
        // of double-charging), but the cashier should be told the state is genuinely unconfirmed.
        setPayOpen(false)
        setPaymentFailure({
          message: 'Unable to reach the payment service. Please try again.',
          certainNotCharged: false,
        })
        return
      }
      if (err.code === 'CHECKOUT_CONCURRENCY_CONFLICT') {
        // The transaction was rolled back before any Sale was written — safe to say for certain
        // that nothing was charged.
        setPayOpen(false)
        setPaymentFailure({ message: "That didn't go through. Please try again.", certainNotCharged: true })
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
      // Any other code we don't specifically recognize — an unmapped backend error, not something
      // to show verbatim (could be an internal detail), and not something we can vouch for the
      // charge state of either.
      setPayOpen(false)
      setPaymentFailure({
        message: "We couldn't complete the payment. Please try again.",
        certainNotCharged: false,
      })
    },
  })

  const resuming = restoredAttempt != null && !cart.isEmpty && status === 'idle'

  return (
    <div className="flex h-full min-h-0 flex-col">
      <PosActionBar
        onNewTransaction={onNewTransaction}
        onVoid={onVoid}
        onDiscounts={() => setDiscountOpen(true)}
        onReturns={() => setReturnLookupOpen(true)}
        onReprint={startReprint}
        onCheckPrice={() => setCheckPriceOpen(true)}
        canVoid={canVoid}
        canReturn={canReturn}
        discountsDisabled={cart.isEmpty}
      />

      <div className="grid min-h-0 flex-1 grid-rows-[minmax(0,1fr)_auto] lg:grid-cols-[65%_35%] lg:grid-rows-1">
        <section className="flex min-h-0 flex-col gap-3 p-4">
          <PosSearchBar ref={searchInputRef} value={term} onChange={setTerm} onEnter={lookupBarcode} />
          <CategoryFilters value={categoryId} onChange={setCategoryId} />
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

        <aside className="min-h-0 p-4 lg:pl-0">
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
        onConfirm={(payment) => mutation.mutate({ payment })}
      />

      <PaymentSuccessModal
        open={successResult != null}
        onClose={() => setSuccessResult(null)}
        result={successResult}
        payment={successPayment}
        onNewTransaction={() => {
          setSuccessResult(null)
          searchInputRef.current?.focus()
        }}
      />

      <PaymentFailedModal
        open={paymentFailure != null}
        onClose={() => setPaymentFailure(null)}
        onTryAgain={() => {
          setPaymentFailure(null)
          setPayOpen(true)
        }}
        amount={totals.grandTotal}
        payment={pendingPaymentRef.current}
        message={paymentFailure?.message ?? ''}
        certainNotCharged={paymentFailure?.certainNotCharged ?? false}
      />

      <DiscountApprovalModal
        open={discountApprovalOpen}
        onClose={() => {
          if (!mutation.isPending) setDiscountApprovalOpen(false)
        }}
        onSubmit={(approval) => {
          if (pendingPaymentRef.current) {
            mutation.mutate({ payment: pendingPaymentRef.current, approval })
          }
        }}
        submitting={mutation.isPending}
        error={discountApprovalError}
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

      <VoidChoiceModal
        open={voidChoiceOpen}
        onClose={() => setVoidChoiceOpen(false)}
        cartItemCount={cart.lines.length}
        onCancelTransaction={() => {
          setVoidChoiceOpen(false)
          setCancelTxnOpen(true)
        }}
        onVoidExistingSale={() => {
          setVoidChoiceOpen(false)
          setVoidLookupOpen(true)
        }}
      />
      <CancelTransactionModal
        open={cancelTxnOpen}
        onClose={() => setCancelTxnOpen(false)}
        branchId={branchId}
        itemCount={cart.lines.length}
        grandTotal={totals.grandTotal}
        onAuthorized={() => {
          confirmNewTransaction()
          toast('success', 'Transaction cancelled')
        }}
      />

      <TransactionLookupModal
        open={voidLookupOpen}
        onClose={() => setVoidLookupOpen(false)}
        title="Void sale"
        actionLabel="Continue to void"
        isEligible={voidEligibility}
        initialSaleNumber={lastSale?.saleNumber}
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

      <ReprintReceiptModal
        open={reprintOpen}
        onClose={() => setReprintOpen(false)}
        suggestedSaleNumber={lastSale?.saleNumber}
      />

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
        onApply={(assignments) => {
          rotateIfNeeded()
          for (const a of assignments) {
            setLineDiscount(a.variantId, a.discount)
          }
        }}
      />
    </div>
  )
}
