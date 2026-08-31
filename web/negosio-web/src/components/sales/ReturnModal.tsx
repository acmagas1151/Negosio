import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { salesApi } from '../../api/pos'
import type { CreateReturnRequest, PaymentMethod, SaleDetailDto } from '../../api/types'
import { POS_PAYMENT_METHODS, PAYMENT_METHOD_LABELS } from '../../lib/pos'
import { returnableQty } from '../../lib/returns'
import { roundMoney } from '../../lib/saleMath'
import { formatMoney, formatQty } from '../../lib/format'
import { useTaxSettings } from '../../hooks/useTaxSettings'
import { Button, Callout, Modal, Select, Table, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  sale: SaleDetailDto
}

export function ReturnModal({ open, onClose, sale }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const tax = useTaxSettings()

  const eligible = useMemo(
    () => sale.items.filter((i) => returnableQty(i) > 0),
    [sale.items],
  )

  const [qty, setQty] = useState<Record<string, string>>({})
  const [restock, setRestock] = useState<Record<string, boolean>>({})
  const [reason, setReason] = useState('')
  const [refundMethod, setRefundMethod] = useState<PaymentMethod>('Cash')
  const [refundReference, setRefundReference] = useState('')
  const [formError, setFormError] = useState('')
  const [conflict, setConflict] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setQty({})
    setRestock(Object.fromEntries(eligible.map((i) => [i.id, true])))
    setReason('')
    setRefundMethod('Cash')
    setRefundReference('')
    setFormError('')
    setConflict('')
  }, [open, eligible])

  const pricesIncludeTax = tax.data?.pricesIncludeTax ?? false
  const estimatedRefund = roundMoney(
    eligible.reduce((sum, i) => {
      const n = Number(qty[i.id])
      if (!(n > 0)) return sum
      const paidPerUnit = (i.netAmount + (pricesIncludeTax ? 0 : i.taxAmount)) / i.quantity
      return sum + paidPerUnit * n
    }, 0),
  )

  const mutation = useMutation({
    mutationFn: () => {
      const items = eligible
        .filter((i) => Number(qty[i.id]) > 0)
        .map((i) => ({
          saleItemId: i.id,
          quantity: Number(qty[i.id]),
          restock: restock[i.id] ?? true,
        }))
      const body: CreateReturnRequest = {
        items,
        reason: reason.trim(),
        refundMethod,
        refundReference: refundReference.trim() || null,
      }
      return salesApi.createReturn(sale.sale.id, body)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['sales', sale.sale.id] })
      qc.invalidateQueries({ queryKey: ['sales'] })
      qc.invalidateQueries({ queryKey: ['inventory'] })
      qc.invalidateQueries({ queryKey: ['dashboard'] })
      toast('success', 'Return recorded')
      onClose()
    },
    onError: (err) => {
      if (err instanceof ApiError) {
        if (err.code === 'RETURN_QUANTITY_EXCEEDED' || err.code === 'RETURN_NOT_ALLOWED') {
          setConflict(err.message)
          qc.invalidateQueries({ queryKey: ['sales', sale.sale.id] })
          return
        }
        if (err.isValidation) {
          setFormError(err.message)
          return
        }
      }
      toast('error', err instanceof Error ? err.message : 'Could not record the return.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setFormError('')

    const rows = eligible.filter((i) => Number(qty[i.id]) > 0)
    if (rows.length === 0) {
      setFormError('Enter a return quantity for at least one item.')
      return
    }
    for (const i of rows) {
      if (Number(qty[i.id]) > returnableQty(i)) {
        setFormError(`Cannot return more than ${formatQty(returnableQty(i))} of ${i.productName}.`)
        return
      }
    }
    if (!reason.trim()) {
      setFormError('A return reason is required.')
      return
    }
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Start a return"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            Record return
          </Button>
        </>
      }
    >
      {conflict && <Callout tone="warning">{conflict}</Callout>}
      {formError && <Callout tone="error">{formError}</Callout>}

      <form onSubmit={submit} className="space-y-4">
        <Table>
          <Table.Head>
            <Table.HeaderCell>Item</Table.HeaderCell>
            <Table.HeaderCell align="right">Purchased</Table.HeaderCell>
            <Table.HeaderCell align="right">Returned</Table.HeaderCell>
            <Table.HeaderCell align="right">Returnable</Table.HeaderCell>
            <Table.HeaderCell align="right">Return qty</Table.HeaderCell>
            <Table.HeaderCell align="right">Restock</Table.HeaderCell>
          </Table.Head>
          <Table.Body>
            {eligible.map((i) => (
              <Table.Row key={i.id}>
                <Table.Cell>
                  <span className="font-medium text-text-primary">{i.productName}</span>
                  {i.variantName && <span className="text-text-muted"> · {i.variantName}</span>}
                </Table.Cell>
                <Table.Cell align="right">{formatQty(i.quantity)}</Table.Cell>
                <Table.Cell align="right">{formatQty(i.returnedQuantity)}</Table.Cell>
                <Table.Cell align="right">{formatQty(returnableQty(i))}</Table.Cell>
                <Table.Cell align="right">
                  <input
                    type="number"
                    min={0}
                    max={returnableQty(i)}
                    step="0.001"
                    value={qty[i.id] ?? ''}
                    onChange={(e) => setQty((p) => ({ ...p, [i.id]: e.target.value }))}
                    aria-label={`Return quantity for ${i.productName}`}
                    className="h-9 w-20 rounded-lg border border-border-strong bg-white px-2 text-right text-sm focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
                  />
                </Table.Cell>
                <Table.Cell align="right">
                  <input
                    type="checkbox"
                    checked={restock[i.id] ?? true}
                    onChange={(e) => setRestock((p) => ({ ...p, [i.id]: e.target.checked }))}
                    aria-label={`Restock ${i.productName}`}
                    className="size-4 rounded border-border-strong text-primary-600"
                  />
                </Table.Cell>
              </Table.Row>
            ))}
          </Table.Body>
        </Table>

        <div className="flex flex-col gap-1.5">
          <label className="text-sm font-semibold text-text-secondary" htmlFor="return-reason">
            Reason
          </label>
          <textarea
            id="return-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            rows={2}
            maxLength={500}
            className="w-full rounded-lg border border-border-strong bg-white px-3 py-2 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
          />
        </div>

        <div className="grid gap-3 sm:grid-cols-2">
          <Select
            label="Refund method"
            name="refundMethod"
            value={refundMethod}
            onChange={(e) => setRefundMethod(e.target.value as PaymentMethod)}
          >
            {POS_PAYMENT_METHODS.map((m) => (
              <option key={m} value={m}>
                {PAYMENT_METHOD_LABELS[m]}
              </option>
            ))}
          </Select>
          <TextField
            label="Refund reference (optional)"
            name="refundReference"
            value={refundReference}
            onChange={(e) => setRefundReference(e.target.value)}
          />
        </div>

        <p className="text-[13px] text-text-muted">
          Estimated refund:{' '}
          <span className="font-semibold text-text-secondary">{formatMoney(estimatedRefund)}</span> —
          the final amount is confirmed by the server.
        </p>
      </form>
    </Modal>
  )
}
