import { ArrowDown, ArrowUp, ChevronsUpDown } from 'lucide-react'
import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'

function Table({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div className="overflow-x-auto rounded-xl border border-border bg-surface">
      <table className={cn('w-full text-sm', className)}>{children}</table>
    </div>
  )
}

function Head({ children }: { children: ReactNode }) {
  return (
    <thead className="border-b border-border bg-surface-subtle">
      <tr>{children}</tr>
    </thead>
  )
}

function Body({ children }: { children: ReactNode }) {
  return <tbody className="divide-y divide-border">{children}</tbody>
}

function Row({ children, className }: { children: ReactNode; className?: string }) {
  return <tr className={cn('hover:bg-surface-subtle/60', className)}>{children}</tr>
}

interface HeaderCellProps {
  children: ReactNode
  align?: 'left' | 'right'
  sortKey?: string
  activeSort?: { by?: string; dir?: 'asc' | 'desc' }
  onSort?: (key: string) => void
}

function HeaderCell({ children, align = 'left', sortKey, activeSort, onSort }: HeaderCellProps) {
  const sortable = !!sortKey && !!onSort
  const isActive = sortable && activeSort?.by === sortKey
  const ariaSort = isActive ? (activeSort?.dir === 'desc' ? 'descending' : 'ascending') : 'none'

  return (
    <th
      scope="col"
      aria-sort={sortable ? ariaSort : undefined}
      className={cn(
        'px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-text-muted',
        align === 'right' ? 'text-right' : 'text-left',
      )}
    >
      {sortable ? (
        <button
          type="button"
          onClick={() => onSort!(sortKey!)}
          className={cn(
            'inline-flex items-center gap-1 hover:text-text-secondary',
            align === 'right' && 'flex-row-reverse',
          )}
        >
          {children}
          {isActive ? (
            activeSort?.dir === 'desc' ? (
              <ArrowDown className="size-3.5" aria-hidden="true" />
            ) : (
              <ArrowUp className="size-3.5" aria-hidden="true" />
            )
          ) : (
            <ChevronsUpDown className="size-3.5 opacity-50" aria-hidden="true" />
          )}
        </button>
      ) : (
        children
      )}
    </th>
  )
}

function Cell({
  children,
  align = 'left',
  className,
}: {
  children: ReactNode
  align?: 'left' | 'right'
  className?: string
}) {
  return (
    <td
      className={cn(
        'px-4 py-3 text-text-secondary',
        align === 'right' ? 'text-right' : 'text-left',
        className,
      )}
    >
      {children}
    </td>
  )
}

Table.Head = Head
Table.Body = Body
Table.Row = Row
Table.HeaderCell = HeaderCell
Table.Cell = Cell

export { Table }
