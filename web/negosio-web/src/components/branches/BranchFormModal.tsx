import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { branchesApi } from '../../api/branches'
import type { BranchDto } from '../../api/types'
import { fieldErrorsFrom } from '../../lib/formErrors'
import { Button, Modal, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  branch: BranchDto | null
}

interface FormState {
  name: string
  code: string
  addressLine1: string
  addressLine2: string
  city: string
  province: string
  postalCode: string
}

const EMPTY: FormState = {
  name: '',
  code: '',
  addressLine1: '',
  addressLine2: '',
  city: '',
  province: '',
  postalCode: '',
}

export function BranchFormModal({ open, onClose, branch }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const editing = branch !== null

  const [form, setForm] = useState<FormState>(EMPTY)
  const [errors, setErrors] = useState<Record<string, string>>({})

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setForm(
      branch
        ? {
            name: branch.name,
            code: branch.code,
            addressLine1: branch.addressLine1,
            addressLine2: branch.addressLine2 ?? '',
            city: branch.city,
            province: branch.province,
            postalCode: branch.postalCode ?? '',
          }
        : EMPTY,
    )
    setErrors({})
  }, [open, branch])

  const set = (key: keyof FormState) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm((f) => ({ ...f, [key]: e.target.value }))

  const mutation = useMutation({
    mutationFn: () => {
      const body = {
        name: form.name.trim(),
        addressLine1: form.addressLine1.trim(),
        addressLine2: form.addressLine2.trim() || null,
        city: form.city.trim(),
        province: form.province.trim(),
        postalCode: form.postalCode.trim() || null,
      }
      return editing
        ? branchesApi.update(branch!.id, body)
        : branchesApi.create({ ...body, code: form.code.trim() })
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['branches'] })
      toast('success', editing ? 'Branch updated' : 'Branch created')
      onClose()
    },
    onError: (error) => {
      if (error instanceof ApiError && error.code === 'DUPLICATE_BRANCH_CODE') {
        setErrors({ code: 'A branch with this code already exists.' })
        return
      }
      const fields = fieldErrorsFrom(error)
      if (Object.keys(fields).length > 0) {
        setErrors(fields)
        return
      }
      toast('error', error instanceof Error ? error.message : 'Something went wrong.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    const next: Record<string, string> = {}
    if (!form.name.trim()) next.name = 'Branch name is required.'
    if (!editing && !form.code.trim()) next.code = 'Branch code is required.'
    if (!form.addressLine1.trim()) next.addressline1 = 'Address line 1 is required.'
    if (!form.city.trim()) next.city = 'City is required.'
    if (!form.province.trim()) next.province = 'Province is required.'
    setErrors(next)
    if (Object.keys(next).length > 0) return
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={editing ? 'Edit branch' : 'New branch'}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            {editing ? 'Save' : 'Create'}
          </Button>
        </>
      }
    >
      <form onSubmit={submit} className="space-y-3">
        <TextField
          label="Name"
          name="name"
          value={form.name}
          maxLength={150}
          onChange={set('name')}
          error={errors.name}
          autoFocus
        />
        <TextField
          label="Code"
          name="code"
          value={form.code}
          maxLength={20}
          onChange={set('code')}
          error={errors.code}
          readOnly={editing}
          disabled={editing}
          hint={editing ? "Branch codes can't be changed." : 'Short, e.g. BGC. Letters, numbers and hyphens.'}
        />
        <TextField label="Address line 1" name="addressLine1" value={form.addressLine1} maxLength={200} onChange={set('addressLine1')} error={errors.addressline1} />
        <TextField label="Address line 2" name="addressLine2" value={form.addressLine2} maxLength={200} onChange={set('addressLine2')} />
        <div className="grid gap-x-3 sm:grid-cols-2">
          <TextField label="City" name="city" value={form.city} maxLength={100} onChange={set('city')} error={errors.city} />
          <TextField label="Province" name="province" value={form.province} maxLength={100} onChange={set('province')} error={errors.province} />
        </div>
        <TextField label="Postal code" name="postalCode" value={form.postalCode} maxLength={20} onChange={set('postalCode')} />
      </form>
    </Modal>
  )
}
