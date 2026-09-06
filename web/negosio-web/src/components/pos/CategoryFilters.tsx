import { useQuery } from '@tanstack/react-query'
import { categoriesApi } from '../../api/catalog'
import { cn } from '../../lib/cn'

interface Props {
  value: string | null
  onChange: (categoryId: string | null) => void
}

/** Real category chips — backed by the same categories the Products page manages, filtered
 * server-side via PosCatalogQuery.CategoryId. No client-only/fake filtering. */
export function CategoryFilters({ value, onChange }: Props) {
  const categories = useQuery({
    queryKey: ['categories', 'active-for-pos'],
    queryFn: () => categoriesApi.listAllActive(),
  })

  if (categories.isPending || categories.isError || categories.data.length === 0) return null

  return (
    <div className="flex flex-wrap gap-1.5" role="group" aria-label="Filter by category">
      <button
        type="button"
        onClick={() => onChange(null)}
        className={cn(
          'shrink-0 rounded-full px-3 py-1.5 text-[13px] font-semibold transition-colors',
          value === null
            ? 'bg-primary-600 text-white'
            : 'border border-transparent bg-surface-subtle text-text-secondary hover:border-border-strong',
        )}
      >
        All
      </button>
      {categories.data.map((c) => (
        <button
          key={c.id}
          type="button"
          onClick={() => onChange(c.id)}
          className={cn(
            'shrink-0 rounded-full px-3 py-1.5 text-[13px] font-semibold transition-colors',
            value === c.id
              ? 'bg-primary-600 text-white'
              : 'bg-surface-subtle text-text-secondary hover:bg-border-light',
          )}
        >
          {c.name}
        </button>
      ))}
    </div>
  )
}
