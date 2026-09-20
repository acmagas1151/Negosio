import { useCallback, useEffect, useMemo, useReducer } from 'react'
import type { DiscountType, PosCatalogItemDto } from '../api/types'
import { posStorage, type CartLine, type TerminalCtx } from '../lib/posStorage'

type Action =
  | { kind: 'add'; item: PosCatalogItemDto }
  | { kind: 'setQty'; variantId: string; qty: number }
  | { kind: 'remove'; variantId: string }
  | { kind: 'setDiscount'; variantId: string; discount: { type: DiscountType; value: number } }
  | { kind: 'clear' }

const round3 = (n: number) => Math.round(n * 1000) / 1000

function reducer(state: CartLine[], action: Action): CartLine[] {
  switch (action.kind) {
    case 'add': {
      const { item } = action
      const existing = state.find((l) => l.variantId === item.productVariantId)
      if (existing) {
        return state.map((l) =>
          l.variantId === item.productVariantId ? { ...l, quantity: round3(l.quantity + 1) } : l,
        )
      }
      return [
        ...state,
        {
          variantId: item.productVariantId,
          productId: item.productId,
          name: item.productName,
          variantName: item.variantName,
          sku: item.sku,
          unitPrice: item.sellingPrice,
          quantity: 1,
          discount: { type: 'None', value: 0 },
        },
      ]
    }
    case 'setQty': {
      const qty = round3(action.qty)
      if (!(qty > 0)) return state.filter((l) => l.variantId !== action.variantId)
      return state.map((l) => (l.variantId === action.variantId ? { ...l, quantity: qty } : l))
    }
    case 'remove':
      return state.filter((l) => l.variantId !== action.variantId)
    case 'setDiscount':
      return state.map((l) =>
        l.variantId === action.variantId ? { ...l, discount: action.discount } : l,
      )
    case 'clear':
      return []
  }
}

export interface PosCart {
  lines: CartLine[]
  addItem: (item: PosCatalogItemDto) => void
  setQty: (variantId: string, qty: number) => void
  removeLine: (variantId: string) => void
  setLineDiscount: (variantId: string, d: { type: DiscountType; value: number }) => void
  clear: () => void
  isEmpty: boolean
}

/**
 * POS cart state (useReducer) mirrored to `localStorage`, scoped to the exact terminal
 * (`tenant/branch/register/session`). Seeded from storage on mount (this component only mounts
 * once its terminal context is fully resolved, so the seed key is stable for its lifetime).
 * Prunes only this register's stale sessions. `clear()` also drops the persisted checkout-attempt id.
 */
export function usePosCart(ctx: TerminalCtx | null): PosCart {
  const [lines, dispatch] = useReducer(
    reducer,
    ctx,
    (c): CartLine[] => (c ? (posStorage.readCart(c) ?? []) : []),
  )

  // Callers pass a memoized ctx, so its identity is stable for a mounted terminal.
  useEffect(() => {
    if (ctx) posStorage.pruneStaleForRegister(ctx)
  }, [ctx])

  useEffect(() => {
    if (!ctx) return
    if (lines.length === 0) posStorage.clearCart(ctx)
    else posStorage.writeCart(ctx, lines)
  }, [ctx, lines])

  const addItem = useCallback((item: PosCatalogItemDto) => dispatch({ kind: 'add', item }), [])
  const setQty = useCallback(
    (variantId: string, qty: number) => dispatch({ kind: 'setQty', variantId, qty }),
    [],
  )
  const removeLine = useCallback((variantId: string) => dispatch({ kind: 'remove', variantId }), [])
  const setLineDiscount = useCallback(
    (variantId: string, d: { type: DiscountType; value: number }) =>
      dispatch({ kind: 'setDiscount', variantId, discount: d }),
    [],
  )
  const clear = useCallback(() => {
    dispatch({ kind: 'clear' })
    if (ctx) {
      posStorage.clearCart(ctx)
      posStorage.clearAttemptId(ctx)
    }
  }, [ctx])

  return useMemo(
    () => ({
      lines,
      addItem,
      setQty,
      removeLine,
      setLineDiscount,
      clear,
      isEmpty: lines.length === 0,
    }),
    [lines, addItem, setQty, removeLine, setLineDiscount, clear],
  )
}
