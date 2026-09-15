import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { PackageSearch } from 'lucide-react'
import { ApiError } from '../../api/client'
import { fulfillmentApi } from '../../api/fulfillment'
import { checkoutApi, posCatalogApi } from '../../api/pos'
import type {
  CheckoutPaymentInput,
  CheckoutRequest,
  DiscountType,
  FulfillmentItemInput,
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
import { calcTotals, roundMoney } from '../../lib/saleMath'
import type { LastSaleRef } from '../../lib/pos'
import {
  EMPTY_DELIVERY_CHARGE,
  emptyFulfillmentSchedule,
  parseDeliveryCharge,
  reconcileSchedules,
  VOID_INELIGIBLE_MESSAGES,
  type FulfillmentSchedule,
} from '../../lib/pos'
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

/** One method's schedule-list state + editing callbacks, built fresh for Delivery and again for
 * Pickup — each method's schedules are entirely independent (separate idempotency keys, separate
 * validation, never merged into one list). */
function useFulfillmentSchedules() {
  const [schedules, setSchedules] = useState<FulfillmentSchedule[]>([emptyFulfillmentSchedule()])

  const onScheduleFieldChange = useCallback(
    (key: string, patch: Partial<Omit<FulfillmentSchedule, 'key' | 'items'>>) =>
      setSchedules((prev) => prev.map((s) => (s.key === key ? { ...s, ...patch } : s))),
    [],
  )
  const onScheduleItemChange = useCallback((key: string, variantId: string, quantity: number) => {
    setSchedules((prev) =>
      prev.map((s) => {
        if (s.key !== key) return s
        const exists = s.items.some((i) => i.variantId === variantId)
        const items = exists
          ? s.items.map((i) => (i.variantId === variantId ? { ...i, quantity } : i))
          : [...s.items, { variantId, quantity }]
        return { ...s, items: items.filter((i) => i.quantity > 0) }
      }),
    )
  }, [])
  const onAddSchedule = useCallback(() => setSchedules((prev) => [...prev, emptyFulfillmentSchedule()]), [])
  const onRemoveSchedule = useCallback(
    (key: string) => setSchedules((prev) => (prev.length > 1 ? prev.filter((s) => s.key !== key) : prev)),
    [],
  )
  const reset = useCallback(() => setSchedules([emptyFulfillmentSchedule()]), [])

  return { schedules, setSchedules, onScheduleFieldChange, onScheduleItemChange, onAddSchedule, onRemoveSchedule, reset }
}

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
  // Delivery/pickup involvement itself lives on the cart lines now (CartItem's per-line
  // allocation) — what's built here is just the SCHEDULE detail (recipient/date/address/notes)
  // for whichever lines the cashier marked. Values live here (not in PaymentModal) so a
  // failed-payment retry keeps them through the modal's reopen. Schedules are submitted only
  // after the sale exists (checkout mutation's onSuccess). Cleared on New Transaction and after a
  // completed sale.
  const [deliveryCharge, setDeliveryCharge] = useState(EMPTY_DELIVERY_CHARGE)
  const delivery = useFulfillmentSchedules()
  const pickup = useFulfillmentSchedules()
  // How many delivery/pickup schedules were created for a just-completed sale — powers the success
  // modal's "View N schedules" affordance. Cleared with the success modal.
  const [successDeliveryCount, setSuccessDeliveryCount] = useState(0)
  const [successPickupCount, setSuccessPickupCount] = useState(0)
  // Only for a genuine payment-attempt failure (network/connectivity, a concurrency conflict, or a
  // truly unexpected error) — insufficient inventory, discount approval, and a lost register
  // session each already have their own targeted recovery flow and never populate this.
  const [paymentFailure, setPaymentFailure] = useState<
    { message: string; certainNotCharged: boolean } | null
  >(null)

  const idRef = useRef<string | null>(null)
  // Two independent idempotency keys — the delivery batch and the pickup batch are never allowed
  // to share one, since retrying one method's batch must never be mistaken (server-side) for a
  // retry of the other's.
  const deliveryBatchIdRef = useRef<string | null>(null)
  const pickupBatchIdRef = useRef<string | null>(null)
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
  const setDeliveryRequired = cart.setDeliveryRequired
  const setPickupRequired = cart.setPickupRequired

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
  // Delivery/pickup allocation is part of the checkout body too (CheckoutItemInput carries both
  // quantities), so editing it rotates a needs-new-id attempt the same as editing qty/discount does.
  const onSetDeliveryRequired = useCallback(
    (variantId: string, quantity: number) => {
      rotateIfNeeded()
      setDeliveryRequired(variantId, quantity)
    },
    [rotateIfNeeded, setDeliveryRequired],
  )
  const onSetPickupRequired = useCallback(
    (variantId: string, quantity: number) => {
      rotateIfNeeded()
      setPickupRequired(variantId, quantity)
    },
    [rotateIfNeeded, setPickupRequired],
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

  const ensureDeliveryBatchId = useCallback(() => {
    if (!deliveryBatchIdRef.current) deliveryBatchIdRef.current = crypto.randomUUID()
    return deliveryBatchIdRef.current
  }, [])
  const ensurePickupBatchId = useCallback(() => {
    if (!pickupBatchIdRef.current) pickupBatchIdRef.current = crypto.randomUUID()
    return pickupBatchIdRef.current
  }, [])

  const resetFulfillmentState = useCallback(() => {
    setDeliveryCharge(EMPTY_DELIVERY_CHARGE)
    delivery.reset()
    pickup.reset()
    deliveryBatchIdRef.current = null
    pickupBatchIdRef.current = null
  }, [delivery, pickup])

  const onDeliveryChargeChange = useCallback((value: string) => setDeliveryCharge(value), [])

  // Delivery/pickup schedules deliberately survive a failed-payment retry (see the comment on their
  // declarations above) — nothing else resets them when the CART changes. But a line removed (or
  // its allocation reduced) after a schedule was drafted against it leaves that schedule stale: a
  // removed variant's schedule item would still be submitted to the batch-create mutation
  // post-checkout even though checkout itself never sent that line (CheckoutRequest.items comes
  // from cart.lines), and posting a stale saleItemId/quantity pair fails the WHOLE batch for a sale
  // that's already been paid for. This reconciles both methods' schedules against the cart's OWN
  // deliveryRequiredQuantity/pickupRequiredQuantity fields (usePosCart already keeps those within
  // `0 <= x <= quantity` on every cart edit) every time the cart changes, so the schedules the
  // cashier sees in the payment modal are never more than one render behind the cart. Reading
  // delivery.schedules/pickup.schedules directly (rather than via a functional updater) is safe
  // because this render's closure already has their latest values; depending on them too would
  // re-run this effect on every schedule-form edit.
  useEffect(() => {
    const deliveryRequired: Record<string, number> = {}
    const pickupRequired: Record<string, number> = {}
    for (const l of cart.lines) {
      if (l.deliveryRequiredQuantity > 0) deliveryRequired[l.variantId] = l.deliveryRequiredQuantity
      if (l.pickupRequiredQuantity > 0) pickupRequired[l.variantId] = l.pickupRequiredQuantity
    }

    const d = reconcileSchedules(delivery.schedules, deliveryRequired)
    if (d.changed) {
      // oxlint-disable-next-line set-state-in-effect
      delivery.setSchedules(d.schedules)
    }
    const p = reconcileSchedules(pickup.schedules, pickupRequired)
    if (p.changed) {
      // oxlint-disable-next-line set-state-in-effect
      pickup.setSchedules(p.schedules)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cart.lines])

  // Schedules the sale's delivery/pickup allocations right after checkout succeeds. A failure here
  // never unwinds the sale — it stays Completed — it only tells the cashier to finish scheduling
  // from the Sale page. Idempotent server-side via each ref's own BatchRequestId: retrying (e.g. the
  // cashier clicking retry after seeing the toast) resubmits the SAME batch id and returns the
  // original rows. The two methods use separate idempotency keys — never one shared id — since a
  // retry of one must never be mistaken, server-side, for a retry of the other.
  const createDeliveryBatchMutation = useMutation({
    mutationFn: ({
      saleId,
      resultItems,
    }: {
      saleId: string
      saleNumber: string
      resultItems: SaleResultDto['items']
    }) => {
      // Defense in depth only — the cart-reconciliation effect above should already guarantee every
      // schedule item's variantId is still on the sale that was just checked out. If a lookup ever
      // misses anyway, drop just that one item (never throw): the sale is already completed and paid
      // for by this point, so a non-null assertion here would take down an otherwise-valid batch over
      // one stale entry. A miss means the reconciliation has a gap, so it's surfaced via a toast.
      let droppedStaleItem = false
      const resolvedSchedules = delivery.schedules
        .map((s) => ({
          ...s,
          items: s.items
            .filter((i) => i.quantity > 0)
            .flatMap((i): FulfillmentItemInput[] => {
              const match = resultItems.find((ri) => ri.productVariantId === i.variantId)
              if (!match) {
                droppedStaleItem = true
                return []
              }
              return [{ saleItemId: match.saleItemId, quantity: i.quantity }]
            }),
        }))
        .filter((s) => s.items.length > 0)

      if (droppedStaleItem) {
        toast(
          'error',
          'One or more delivery items no longer matched the completed sale and were skipped. Please verify the delivery schedule.',
        )
      }

      return fulfillmentApi.createDeliveryBatch(saleId, {
        batchRequestId: ensureDeliveryBatchId(),
        schedules: resolvedSchedules.map((s) => ({
          scheduledDate: s.scheduledDate,
          recipientName: s.recipientName.trim(),
          deliveryAddress: s.deliveryAddress.trim(),
          contactNumber: s.contactNumber.trim() || null,
          notes: s.notes.trim() || null,
          items: s.items,
        })),
      })
    },
    onSuccess: (result) => setSuccessDeliveryCount(result.created.length),
    onError: (_err, variables) => {
      // Never a modal implying the payment failed — the sale already succeeded by this point. A
      // toast naming the sale number, pointing at where to finish, is the whole recovery story.
      toast(
        'error',
        `Sale #${variables.saleNumber} completed, but the delivery schedule could not be created. Schedule it from the sale's detail page.`,
      )
    },
  })

  const createPickupBatchMutation = useMutation({
    mutationFn: ({
      saleId,
      resultItems,
    }: {
      saleId: string
      saleNumber: string
      resultItems: SaleResultDto['items']
    }) => {
      // Same defense-in-depth reconciliation as the delivery batch above.
      let droppedStaleItem = false
      const resolvedSchedules = pickup.schedules
        .map((s) => ({
          ...s,
          items: s.items
            .filter((i) => i.quantity > 0)
            .flatMap((i): FulfillmentItemInput[] => {
              const match = resultItems.find((ri) => ri.productVariantId === i.variantId)
              if (!match) {
                droppedStaleItem = true
                return []
              }
              return [{ saleItemId: match.saleItemId, quantity: i.quantity }]
            }),
        }))
        .filter((s) => s.items.length > 0)

      if (droppedStaleItem) {
        toast(
          'error',
          'One or more pickup items no longer matched the completed sale and were skipped. Please verify the pickup schedule.',
        )
      }

      return fulfillmentApi.createPickupBatch(saleId, {
        batchRequestId: ensurePickupBatchId(),
        schedules: resolvedSchedules.map((s) => ({
          scheduledDate: s.scheduledDate,
          recipientName: s.recipientName.trim(),
          contactNumber: s.contactNumber.trim() || null,
          notes: s.notes.trim() || null,
          items: s.items,
        })),
      })
    },
    onSuccess: (result) => setSuccessPickupCount(result.created.length),
    onError: (_err, variables) => {
      toast(
        'error',
        `Sale #${variables.saleNumber} completed, but the pickup schedule could not be created. Schedule it from the sale's detail page.`,
      )
    },
  })

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
    resetFulfillmentState()
    setSuccessDeliveryCount(0)
    setSuccessPickupCount(0)
  }, [cart, resetFulfillmentState])

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
      const anyDeliveryRequired = cart.lines.some((l) => l.deliveryRequiredQuantity > 0)
      const body: CheckoutRequest = {
        branchId,
        registerSessionId: ctx.registerSessionId,
        clientRequestId,
        items: cart.lines.map((l) => ({
          productVariantId: l.variantId,
          quantity: l.quantity,
          discount: l.discount.type === 'None' ? null : l.discount,
          deliveryRequiredQuantity: l.deliveryRequiredQuantity,
          pickupRequiredQuantity: l.pickupRequiredQuantity,
        })),
        payments: [payment],
        deliveryCharge: roundMoney(parseDeliveryCharge(anyDeliveryRequired, deliveryCharge)),
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

      // The sale exists now, so submit whichever schedules the cashier built — up to two batches,
      // each with its own idempotency key. A batch failure only surfaces a toast; it never touches
      // the sale, which has already succeeded by this point.
      const activeDeliverySchedules = delivery.schedules.filter((s) => s.items.some((i) => i.quantity > 0))
      if (activeDeliverySchedules.length > 0) {
        createDeliveryBatchMutation.mutate({
          saleId: result.saleId,
          saleNumber: result.saleNumber,
          resultItems: result.items,
        })
      } else {
        setSuccessDeliveryCount(0)
      }
      const activePickupSchedules = pickup.schedules.filter((s) => s.items.some((i) => i.quantity > 0))
      if (activePickupSchedules.length > 0) {
        createPickupBatchMutation.mutate({
          saleId: result.saleId,
          saleNumber: result.saleNumber,
          resultItems: result.items,
        })
      } else {
        setSuccessPickupCount(0)
      }
      resetFulfillmentState()
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
        cartLines={cart.lines}
        delivery={{
          schedules: delivery.schedules,
          onScheduleFieldChange: delivery.onScheduleFieldChange,
          onScheduleItemChange: delivery.onScheduleItemChange,
          onAddSchedule: delivery.onAddSchedule,
          onRemoveSchedule: delivery.onRemoveSchedule,
        }}
        pickup={{
          schedules: pickup.schedules,
          onScheduleFieldChange: pickup.onScheduleFieldChange,
          onScheduleItemChange: pickup.onScheduleItemChange,
          onAddSchedule: pickup.onAddSchedule,
          onRemoveSchedule: pickup.onRemoveSchedule,
        }}
        deliveryCharge={deliveryCharge}
        onDeliveryChargeChange={onDeliveryChargeChange}
        onSetDeliveryRequired={onSetDeliveryRequired}
        onSetPickupRequired={onSetPickupRequired}
      />

      <PaymentSuccessModal
        open={successResult != null}
        onClose={() => {
          setSuccessResult(null)
          setSuccessDeliveryCount(0)
          setSuccessPickupCount(0)
        }}
        result={successResult}
        payment={successPayment}
        deliveryCount={successDeliveryCount}
        pickupCount={successPickupCount}
        onNewTransaction={() => {
          setSuccessResult(null)
          setSuccessDeliveryCount(0)
          setSuccessPickupCount(0)
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
        amount={
          totals.grandTotal +
          roundMoney(parseDeliveryCharge(cart.lines.some((l) => l.deliveryRequiredQuantity > 0), deliveryCharge))
        }
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
