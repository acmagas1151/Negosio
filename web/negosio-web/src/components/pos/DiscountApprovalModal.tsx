import { useEffect, useState } from 'react'
import { Button, Callout, Modal, TextField } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  onSubmit: (approval: { approverEmail: string; approverPassword: string }) => void
  submitting: boolean
  error: string | null
}

/**
 * Collects Manager/Admin/Owner credentials to retry a checkout that was rejected with
 * DISCOUNT_APPROVAL_REQUIRED — this sale carries a line discount (from either the whole-cart tool
 * or a per-line edit) that the cashier can't apply directly. Unlike VoidSaleModal/OpenCashDrawerModal
 * this doesn't own its own mutation: submitting just hands the credentials back to the parent, which
 * retries the same checkout call with them attached.
 */
export function DiscountApprovalModal({ open, onClose, onSubmit, submitting, error }: Props) {
  const [approverEmail, setApproverEmail] = useState('')
  const [approverPassword, setApproverPassword] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setApproverEmail('')
    setApproverPassword('')
  }, [open])

  const canSubmit = approverEmail.trim() !== '' && approverPassword !== '' && !submitting

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Discount approval required"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={submitting}>
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => onSubmit({ approverEmail, approverPassword })}
            loading={submitting}
            disabled={!canSubmit}
          >
            Approve & charge
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      <Callout tone="info">
        This sale includes a discount you don&rsquo;t have permission to apply directly. An
        authorized Manager, Admin, or Owner must approve it before the sale can complete.
      </Callout>

      <TextField
        label="Manager account"
        name="approverEmail"
        type="email"
        value={approverEmail}
        onChange={(e) => setApproverEmail(e.target.value)}
        autoFocus
      />
      <TextField
        label="Password"
        name="approverPassword"
        type="password"
        value={approverPassword}
        onChange={(e) => setApproverPassword(e.target.value)}
      />
    </Modal>
  )
}
