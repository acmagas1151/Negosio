import { useEffect, useRef } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { deliveryReceiptsApi } from '../api/deliveryReceipts'
import { formatMoney, formatQty } from '../lib/format'
import { Button, ErrorState, LoadingState } from '../components/ui'
import { deliveryReceiptCss } from '../components/receipt/deliveryReceiptStyles'
import { ReceiptHeader } from '../components/receipt/ReceiptHeader'
import { ReceiptFooter } from '../components/receipt/ReceiptFooter'

export default function DeliveryReceiptPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const autoPrint = params.get('print') === '1'
  const printedRef = useRef(false)

  const query = useQuery({
    queryKey: ['delivery-receipts', id],
    queryFn: () => deliveryReceiptsApi.get(id),
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

  const d = query.data
  const when = new Date(d.createdAtUtc).toLocaleString()
  const hasCustomHeader = !!(d.headerText && d.headerText.trim())
  const showContact = d.showContactNumber && !!d.contactNumber
  const showRelatedSale = d.showRelatedSaleNumber && !!d.relatedSaleNumber
  const showPriceColumns = d.showPrices

  return (
    <div className="dr-page">
      <style>{deliveryReceiptCss()}</style>
      <div className="dr">
        <ReceiptHeader
          headerText={d.headerText}
          business={{
            businessName: d.businessName,
            branchName: d.branchName,
            address: d.businessAddress,
            contactNumber: d.businessContactNumber,
            taxId: d.taxId,
            showBranch: false,
          }}
        />
        {!hasCustomHeader && <p className="dr-title">DELIVERY RECEIPT</p>}
        <hr />

        <div className="dr-meta">
          <span>Date: {when}</span>
          <span>Branch: {d.branchName}</span>
        </div>

        <div className="dr-recipient">
          <div className="dr-line">
            <span className="label">Recipient:</span> {d.recipientName}
          </div>
          <div className="dr-line">
            <span className="label">Address:</span> {d.deliveryAddress}
          </div>
          {showContact && (
            <div className="dr-line">
              <span className="label">Contact:</span> {d.contactNumber}
            </div>
          )}
          {showRelatedSale && (
            <div className="dr-line">
              <span className="label">Related Sale:</span> #{d.relatedSaleNumber}
            </div>
          )}
        </div>

        <table className="dr-items">
          <thead>
            <tr>
              <th className="num">Qty</th>
              <th>Product</th>
              {showPriceColumns && <th className="num">Unit Price</th>}
              {showPriceColumns && <th className="num">Amount</th>}
            </tr>
          </thead>
          <tbody>
            {d.items.map((item, i) => (
              <tr key={i}>
                <td className="num">{formatQty(item.quantity)}</td>
                <td>
                  {item.productName}
                  {item.variantName ? ` (${item.variantName})` : ''}
                </td>
                {showPriceColumns && (
                  <td className="num">{item.unitPrice != null ? formatMoney(item.unitPrice) : ''}</td>
                )}
                {showPriceColumns && (
                  <td className="num">{item.amount != null ? formatMoney(item.amount) : ''}</td>
                )}
              </tr>
            ))}
          </tbody>
        </table>

        {d.deliveryNotes && d.deliveryNotes.trim() && (
          <div className="dr-notes">
            <span className="label">Delivery notes:</span> {d.deliveryNotes}
          </div>
        )}

        {d.showSignatureFields && (
          <div className="dr-signatures">
            <div className="dr-sig">
              <span className="name">Prepared by: {d.preparedByName}</span>
            </div>
            <div className="dr-sig">
              Delivered by:
              <span className="dr-sig-rule" />
            </div>
            <div className="dr-sig">
              Received by:
              <span className="dr-sig-rule" />
            </div>
            <div className="dr-sig">
              Date received:
              <span className="dr-sig-rule" />
            </div>
          </div>
        )}

        {d.footerText && <ReceiptFooter footerText={d.footerText} />}
      </div>

      <div className="dr-actions">
        <Button onClick={() => window.print()}>Print</Button>
        <Button variant="secondary" onClick={() => navigate(-1)}>
          Back
        </Button>
      </div>
    </div>
  )
}
