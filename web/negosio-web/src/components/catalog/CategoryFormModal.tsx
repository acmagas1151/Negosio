import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { categoriesApi } from '../../api/catalog'
import { ApiError } from '../../api/client'
import type { CategoryDto } from '../../api/types'
import { fieldErrorsFrom, mapCodeToField } from '../../lib/formErrors'
import { Button, Modal, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  category: CategoryDto | null
}

export function CategoryFormModal({ open, onClose, category }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [nameError, setNameError] = useState('')

  // Re-seed the form whenever it opens for a different category (or for create).
  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setName(category?.name ?? '')
    setDescription(category?.description ?? '')
    setNameError('')
  }, [open, category])

  const mutation = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), description: description.trim() || null }
      return category
        ? categoriesApi.update(category.id, { ...body, isActive: category.isActive })
        : categoriesApi.create(body)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['categories'] })
      toast('success', category ? 'Changes saved' : 'Category created')
      onClose()
    },
    onError: (error) => {
      const fields = fieldErrorsFrom(error)
      const codeField = error instanceof ApiError ? mapCodeToField(error.code) : null
      if (fields.name) setNameError(fields.name)
      else if (codeField === 'name') setNameError('A category with this name already exists.')
      else toast('error', error instanceof Error ? error.message : 'Something went wrong.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    setNameError('')
    if (!name.trim()) {
      setNameError('Name is required.')
      return
    }
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={category ? 'Edit category' : 'New category'}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            {category ? 'Save' : 'Create'}
          </Button>
        </>
      }
    >
      <form onSubmit={submit} className="space-y-1">
        <TextField
          label="Name"
          name="name"
          value={name}
          maxLength={80}
          onChange={(e) => setName(e.target.value)}
          error={nameError || undefined}
          autoFocus
        />
        <label className="text-sm font-semibold text-text-secondary" htmlFor="category-description">
          Description
        </label>
        <textarea
          id="category-description"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={3}
          maxLength={500}
          className="mt-1.5 w-full rounded-lg border border-border-strong bg-white px-3 py-2 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
        />
      </form>
    </Modal>
  )
}
