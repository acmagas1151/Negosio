interface ReceiptBusinessInfoProps {
  businessName: string
  branchName: string
  address: string | null
  contactNumber: string | null
  taxId: string | null
  showBranch: boolean
}

export function ReceiptBusinessInfo({
  businessName,
  branchName,
  address,
  contactNumber,
  taxId,
  showBranch,
}: ReceiptBusinessInfoProps) {
  return (
    <>
      <h1>{businessName}</h1>
      {showBranch && branchName && <p className="center muted">{branchName}</p>}
      {address && <p className="center muted">{address}</p>}
      {contactNumber && <p className="center muted">{contactNumber}</p>}
      {taxId && <p className="center muted">TIN: {taxId}</p>}
    </>
  )
}
