import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { PackageSearch } from 'lucide-react'
import { ApiError } from '../../api/client'
import { posCatalogApi } from '../../api/pos'
import type { CartLine, TerminalCtx } from '../../lib/posStorage'
import { usePosCart } from '../../hooks/usePosCart'
import { useBarcodeScanner } from '../../hooks/useBarcodeScanner'
import { useDebouncedValue } from '../../hooks/useDebouncedValue'
import { useTaxSettings } from '../../hooks/useTaxSettings'
import { calcTotals } from '../../lib/saleMath'
import { EmptyState, ErrorState, Pagination, SkeletonText, useToast } from '../ui'
import { CartPanel } from './CartPanel'
import { PosProductGrid } from './PosProductGrid'
import { PosSearchBar } from './PosSearchBar'

const PAGE_SIZE = 24

export interface CheckoutAttempt {
  lines: CartLine[]
  previewTotal: number
}

interface Props {
  ctx: TerminalCtx
  branchId: string
  onCheckoutRequested: (payload: CheckoutAttempt) => void
}

export function PosTerminal({ ctx, branchId, onCheckoutRequested }: Props) {
  const cart = usePosCart(ctx)
  const tax = useTaxSettings()
  const { toast } = useToast()
  const [term, setTerm] = useState('')
  const [page, setPage] = useState(1)
  const debounced = useDebouncedValue(term, 300)

  useEffect(() => {
    // oxlint-disable-next-line set-state-in-effect
    setPage(1)
  }, [debounced])

  const catalog = useQuery({
    queryKey: ['pos-catalog', { branchId, search: debounced, page }],
    queryFn: () =>
      posCatalogApi.search({
        branchId,
        search: debounced || undefined,
        page,
        pageSize: PAGE_SIZE,
      }),
  })

  async function lookupBarcode(code: string) {
    if (!code) return
    try {
      const item = await posCatalogApi.barcode(code, branchId)
      cart.addItem(item)
      toast('success', `Added ${item.productName}`)
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) {
        toast('info', `No product for barcode ${code}`)
      } else {
        toast('error', e instanceof Error ? e.message : 'Lookup failed')
      }
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

  return (
    <div className="grid h-full min-h-0 grid-rows-[minmax(0,1fr)_auto] lg:grid-cols-[62%_38%] lg:grid-rows-1">
      <section className="flex min-h-0 flex-col gap-3 p-4">
        <PosSearchBar value={term} onChange={setTerm} onEnter={lookupBarcode} />
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
            <PosProductGrid items={catalog.data.items} onAdd={cart.addItem} />
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
          onSetQty={cart.setQty}
          onRemove={cart.removeLine}
          onSetDiscount={cart.setLineDiscount}
          onCharge={() => onCheckoutRequested({ lines: cart.lines, previewTotal: totals.grandTotal })}
          chargeDisabled={false}
        />
      </aside>
    </div>
  )
}
