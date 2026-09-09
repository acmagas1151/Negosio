import type { UpdateReceiptSettingsRequest } from '../../api/types'
import { ReceiptFooter } from '../receipt/ReceiptFooter'
import { ReceiptHeader } from '../receipt/ReceiptHeader'
import { thermalReceiptCss } from '../receipt/receiptStyles'

interface PreviewBusiness {
  businessName: string
  branchName: string
  address: string | null
  contactNumber: string | null
  taxId: string | null
}

interface ReceiptPreviewProps {
  kind: 'sales' | 'delivery'
  values: UpdateReceiptSettingsRequest
  business: PreviewBusiness
}

/**
 * Config-only preview of a printed receipt. Pure function of props — no fetch, no query,
 * no `dangerouslySetInnerHTML`. Item / total rows are mock data; only the toggles and
 * header/footer text come from the (unsaved) form `values`.
 */
export function ReceiptPreview({ kind, values, business }: ReceiptPreviewProps) {
  return (
    <div className="rounded-xl border border-border bg-surface-subtle p-4">
      <p className="mb-3 text-[11px] font-semibold uppercase tracking-wide text-text-muted">
        Preview — not saved
      </p>
      <div className="overflow-x-auto">
        {kind === 'sales' ? (
          <SalesPreview values={values} business={business} />
        ) : (
          <DeliveryPreview values={values} business={business} />
        )}
      </div>
    </div>
  )
}

function SalesPreview({ values, business }: { values: UpdateReceiptSettingsRequest; business: PreviewBusiness }) {
  return (
    <>
      <style>{thermalReceiptCss(values.width)}</style>
      <div className="receipt" style={{ transform: 'scale(0.9)', transformOrigin: 'top left' }}>
        <ReceiptHeader
          headerText={values.salesHeaderText}
          business={{ ...business, showBranch: values.salesShowBranch }}
        />
        <hr />
        {values.salesShowCashier && (
          <div className="row">
            <span>Cashier</span>
            <span className="r">Sample Cashier</span>
          </div>
        )}
        <div className="row">
          <span>Receipt</span>
          <span className="r bold">#000123</span>
        </div>
        <hr />
        <div className="item">
          <div>Sample item</div>
          <div className="row">
            <span className="muted">1 × 100.00</span>
            <span className="r">100.00</span>
          </div>
        </div>
        <div className="item">
          <div>Another item</div>
          <div className="row">
            <span className="muted">2 × 50.00</span>
            <span className="r">100.00</span>
          </div>
        </div>
        <hr />
        <div className="row">
          <span>Subtotal</span>
          <span className="r">200.00</span>
        </div>
        {values.salesShowTaxLine && (
          <div className="row">
            <span>Tax</span>
            <span className="r">21.43</span>
          </div>
        )}
        <div className="row bold">
          <span>TOTAL</span>
          <span className="r">200.00</span>
        </div>
        <hr />
        <div className="row">
          <span>{values.salesShowPaymentMethod ? 'Cash' : 'Payment'}</span>
          <span className="r">200.00</span>
        </div>
        {values.salesShowReferenceNumber && (
          <div className="row">
            <span className="muted">Ref</span>
            <span className="r muted">—</span>
          </div>
        )}
        <hr />
        <ReceiptFooter footerText={values.salesFooterText} />
      </div>
    </>
  )
}

function DeliveryPreview({ values, business }: { values: UpdateReceiptSettingsRequest; business: PreviewBusiness }) {
  const title = values.deliveryHeaderText?.trim() || 'DELIVERY RECEIPT'
  return (
    <div className="min-w-[260px] max-w-sm rounded-lg border border-border bg-white p-4 font-mono text-[12px] leading-relaxed text-text-primary">
      <p className="text-center text-sm font-bold">{business.businessName || 'Business name'}</p>
      {values.deliveryShowContactNumber && business.contactNumber && (
        <p className="text-center text-text-muted">{business.contactNumber}</p>
      )}
      {business.taxId && <p className="text-center text-text-muted">TIN: {business.taxId}</p>}
      <p className="mt-2 text-center font-bold">{title}</p>
      <hr className="my-2 border-dashed border-border" />
      <p>Recipient: Juan Dela Cruz</p>
      {values.deliveryShowContactNumber && <p>Contact: 0917 000 0000</p>}
      {values.deliveryShowRelatedSaleNumber && <p>Related Sale #0000042</p>}
      <hr className="my-2 border-dashed border-border" />
      <table className="w-full">
        <thead>
          <tr className="text-left">
            <th className="pr-2">Qty</th>
            <th>Product</th>
            {values.deliveryShowPrices && <th className="pl-2 text-right">Unit</th>}
            {values.deliveryShowPrices && <th className="pl-2 text-right">Amount</th>}
          </tr>
        </thead>
        <tbody>
          <tr>
            <td className="pr-2">1</td>
            <td>Sample item</td>
            {values.deliveryShowPrices && <td className="pl-2 text-right">100.00</td>}
            {values.deliveryShowPrices && <td className="pl-2 text-right">100.00</td>}
          </tr>
          <tr>
            <td className="pr-2">2</td>
            <td>Another item</td>
            {values.deliveryShowPrices && <td className="pl-2 text-right">50.00</td>}
            {values.deliveryShowPrices && <td className="pl-2 text-right">100.00</td>}
          </tr>
        </tbody>
      </table>
      {values.deliveryShowSignatureFields && (
        <div className="mt-6 grid grid-cols-2 gap-4">
          <div>
            <div className="border-t border-border pt-1 text-center text-text-muted">Released by</div>
          </div>
          <div>
            <div className="border-t border-border pt-1 text-center text-text-muted">Received by</div>
          </div>
        </div>
      )}
      {values.deliveryFooterText?.trim() && (
        <p className="mt-3 text-center text-text-muted">{values.deliveryFooterText}</p>
      )}
    </div>
  )
}
