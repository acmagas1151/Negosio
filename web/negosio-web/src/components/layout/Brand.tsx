import { cn } from '../../lib/cn'

interface BrandProps {
  /** Size of the mark + wordmark. */
  size?: 'sm' | 'md'
  className?: string
}

/** Negosio wordmark: blue "N" mark + dark-navy text. */
export function Brand({ size = 'md', className }: BrandProps) {
  const mark = size === 'sm' ? 'size-7 text-sm' : 'size-8 text-base'
  const text = size === 'sm' ? 'text-base' : 'text-lg'
  return (
    <span className={cn('inline-flex items-center gap-2', className)}>
      <span
        className={cn(
          'inline-flex items-center justify-center rounded-lg bg-primary-600 font-bold text-white',
          mark,
        )}
        aria-hidden="true"
      >
        N
      </span>
      <span className={cn('font-bold tracking-tight text-text-primary', text)}>Negosio</span>
    </span>
  )
}
