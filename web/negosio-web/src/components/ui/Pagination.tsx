import { ChevronLeft, ChevronRight } from 'lucide-react'
import { Button } from './Button'

interface PaginationProps {
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
  onPageChange: (page: number) => void
}

export function Pagination({ page, pageSize, totalCount, totalPages, onPageChange }: PaginationProps) {
  if (totalPages <= 1) return null

  const first = (page - 1) * pageSize + 1
  const last = Math.min(page * pageSize, totalCount)

  return (
    <div className="flex items-center justify-between gap-4 pt-3 text-sm text-text-muted">
      <p>
        {first}–{last} of {totalCount}
      </p>
      <div className="flex gap-2">
        <Button
          variant="secondary"
          size="sm"
          onClick={() => onPageChange(page - 1)}
          disabled={page <= 1}
          leadingIcon={<ChevronLeft className="size-4" aria-hidden="true" />}
        >
          Previous
        </Button>
        <Button
          variant="secondary"
          size="sm"
          onClick={() => onPageChange(page + 1)}
          disabled={page >= totalPages}
          trailingIcon={<ChevronRight className="size-4" aria-hidden="true" />}
        >
          Next
        </Button>
      </div>
    </div>
  )
}
