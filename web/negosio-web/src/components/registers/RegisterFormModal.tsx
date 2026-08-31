import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { registersApi } from '../../api/pos'
import type { BranchDto, RegisterDto } from '../../api/types'
import { fieldErrorsFrom } from '../../lib/formErrors'
import { Button, Modal, Select, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  register: RegisterDto | null
  branches: BranchDto[]
}

export function RegisterFormModal({ open, onClose, register, branches }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const [name, setName] = useState('')
  const [code, setCode] = useState('')
  const [branchId, setBranchId] = useState('')
  const [isActive, setIsActive] = useState(true)
  const [nameError, setNameError] = useState('')
  const [codeError, setCodeError] = useState('')
  const [branchError, setBranchError] = useState('')

  const soleBranch = branches.length === 1 ? branches[0] : null

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setName(register?.name ?? '')
    setCode(register?.code ?? '')
    setBranchId(register?.branchId ?? soleBranch?.id ?? '')
    setIsActive(register?.isActive ?? true)
    setNameError('')
    setCodeError('')
    setBranchError('')
  }, [open, register, soleBranch?.id])

  const mutation = useMutation({
    mutationFn: () => {
      const trimmed = { name: name.trim(), code: code.trim() }
      return register
        ? registersApi.update(register.id, { ...trimmed, isActive })
        : registersApi.create({ ...trimmed, branchId })
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['registers'] })
      toast('success', register ? 'Changes saved' : 'Register created')
      onClose()
    },
    onError: (error) => {
      if (
        error instanceof ApiError &&
        error.status === 409 &&
        error.code === 'REGISTER_ALREADY_EXISTS'
      ) {
        setCodeError('A register with this code already exists in this branch.')
        return
      }
      const fields = fieldErrorsFrom(error)
      if (fields.name) setNameError(fields.name)
      if (fields.code) setCodeError(fields.code)
      if (fields.branchid) setBranchError(fields.branchid)
      if (!fields.name && !fields.code && !fields.branchid) {
        toast('error', error instanceof Error ? error.message : 'Something went wrong.')
      }
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setNameError('')
    setCodeError('')
    setBranchError('')
    if (!name.trim()) {
      setNameError('Register name is required.')
      return
    }
    if (!code.trim()) {
      setCodeError('Register code is required.')
      return
    }
    if (!register && !branchId) {
      setBranchError('Choose a branch.')
      return
    }
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={register ? 'Edit register' : 'New register'}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            {register ? 'Save' : 'Create'}
          </Button>
        </>
      }
    >
      <form onSubmit={submit} className="space-y-3">
        <TextField
          label="Name"
          name="name"
          value={name}
          maxLength={100}
          onChange={(e) => setName(e.target.value)}
          error={nameError || undefined}
          autoFocus
        />
        <TextField
          label="Code"
          name="code"
          value={code}
          maxLength={20}
          onChange={(e) => setCode(e.target.value)}
          error={codeError || undefined}
          hint="A short identifier, e.g. FC1. Must be unique within the branch."
        />
        {!register && branches.length > 1 && (
          <Select
            label="Branch"
            name="branchId"
            value={branchId}
            onChange={(e) => setBranchId(e.target.value)}
            error={branchError || undefined}
          >
            <option value="">Select a branch</option>
            {branches.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name}
              </option>
            ))}
          </Select>
        )}
        {register && (
          <label className="flex items-center gap-2 text-sm text-text-secondary">
            <input
              type="checkbox"
              checked={isActive}
              onChange={(e) => setIsActive(e.target.checked)}
              className="size-4 rounded border-border-strong text-primary-600 focus:ring-primary-500/30"
            />
            Active
          </label>
        )}
      </form>
    </Modal>
  )
}
