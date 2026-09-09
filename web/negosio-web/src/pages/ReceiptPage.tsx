import { Fragment, useEffect, useRef } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { salesApi } from '../api/pos'
import { PAYMENT_METHOD_LABELS, SALE_STATUS_LABELS } from '../lib/pos'
import type { PaymentMethod } from '../api/types'
import { formatMoney, formatQty } from '../lib/format'
import { Button, ErrorState, LoadingState } from '../components/ui'
import { thermalReceiptCss } from '../components/receipt/receiptStyles'
import { ReceiptHeader } from '../components/receipt/ReceiptHeader'
import { ReceiptFooter } from '../components/receipt/ReceiptFooter'

export default function ReceiptPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const autoPrint = params.get('print') === '1'
  const printedRef = useRef(false)

  const query = useQuery({
    queryKey: ['sales', id, 'receipt'],
    queryFn: () => salesApi.receipt(id),
    enabled: !!id,
  })

  useEffect(() => {
    if (!autoPrint || printedRef.current || !query.isSuccess) return
    printedRef.current = true
    const raf = requestAnimationFrame(() => window.print())
    return () => cancelAnimationFrame(raf)
  }, [autoPrint, query.isSuccess])

  if (query.isPending) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <LoadingState />
      </div>
    )
  }
  if (query.isError) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background p-4">
        <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
      </div>
    )
  }

  const r = query.data
  const when = new Date(r.createdAtUtc).toLocaleString()
  const refundedBanner =
    r.status === 'Refunded' || r.status === 'PartiallyRefunded'
      ? SALE_STATUS_LABELS[r.status].toUpperCase()
      : null
  const voidedBanner = r.status === 'Voided' ? 'VOID — SALE CANCELLED' : null
  const anyTendered = r.payments.some((p) => p.receivedAmount != null)

  return (
    <div className="receipt-page">
      <style>{thermalReceiptCss(r.width)}</style>
      <div className="receipt">
        <ReceiptHeader
          headerText={r.headerText}
          business={{
            businessName: r.storeName,
            branchName: r.branchName,
            address: r.businessAddress,
            contactNumber: r.businessContactNumber,
            taxId: r.taxId,
            showBranch: r.showBranch,
          }}
        />
        <hr />
        <div className="row">
          <span>Receipt</span>
          <span className="r bold">#{r.saleNumber}</span>
        </div>
        <div className="row">
          <span>Register</span>
          <span className="r">{r.registerName}</span>
        </div>
        {r.showCashier && (
          <div className="row">
            <span>Cashier</span>
            <span className="r">{r.cashierName}</span>
          </div>
        )}
        <div className="row">
          <span>Date</span>
          <span className="r">{when}</span>
        </div>
        <hr />
        {r.lines.map((line, i) => (
          <div className="item" key={i}>
            <div>
              {line.description}
              {line.variantName ? ` (${line.variantName})` : ''}
            </div>
            <div className="row">
              <span className="muted">
                {formatQty(line.quantity)} × {formatMoney(line.unitPrice)}
              </span>
              <span className="r">{formatMoney(line.netAmount)}</span>
            </div>
          </div>
        ))}
        <hr />
        <div className="row">
          <span>Subtotal</span>
          <span className="r">{formatMoney(r.subtotal)}</span>
        </div>
        {r.discountTotal > 0 && (
          <div className="row">
            <span>Discount</span>
            <span className="r">−{formatMoney(r.discountTotal)}</span>
          </div>
        )}
        {r.showTaxLine && (
          <div className="row">
            <span>Tax</span>
            <span className="r">{formatMoney(r.taxTotal)}</span>
          </div>
        )}
        <div className="row bold">
          <span>TOTAL</span>
          <span className="r">{formatMoney(r.grandTotal)}</span>
        </div>
        <hr />
        {r.payments.map((p, i) => (
          <Fragment key={i}>
            <div className="row">
              <span>
                {r.showPaymentMethod
                  ? (PAYMENT_METHOD_LABELS[p.method as PaymentMethod] ?? p.method)
                  : 'Payment'}
              </span>
              <span className="r">{formatMoney(p.amount)}</span>
            </div>
            {r.showReferenceNumber && p.referenceNumber && (
              <div className="row">
                <span className="muted">Ref</span>
                <span className="r muted">{p.referenceNumber}</span>
              </div>
            )}
            {p.receivedAmount != null && (
              <>
                <div className="row">
                  <span>Tendered</span>
                  <span className="r">{formatMoney(p.receivedAmount)}</span>
                </div>
                <div className="row">
                  <span>Change</span>
                  <span className="r">{formatMoney(p.changeAmount ?? 0)}</span>
                </div>
              </>
            )}
          </Fragment>
        ))}
        {!anyTendered && (
          <div className="row">
            <span>Change</span>
            <span className="r">{formatMoney(r.changeDue)}</span>
          </div>
        )}
        {refundedBanner && <div className="banner">{refundedBanner}</div>}
        {voidedBanner && <div className="banner-void">{voidedBanner}</div>}
        <hr />
        <ReceiptFooter footerText={r.footerText} />
      </div>

      <div className="receipt-actions">
        <Button onClick={() => window.print()}>Print</Button>
        <Button variant="secondary" onClick={() => navigate(-1)}>
          Back
        </Button>
      </div>
    </div>
  )
}
