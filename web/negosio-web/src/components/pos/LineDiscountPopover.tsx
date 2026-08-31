import { useEffect, useRef, useState } from 'react'
import type { DiscountType } from '../../api/types'
import { Button, Select } from '../ui'
import { inputClass } from '../ui/TextField'
import { cn } from '../../lib/cn'

interface Props {
  discount: { type: DiscountType; value: number }
  onChange: (d: { type: DiscountType; value: number }) => void
  onClose: () => void
}

export function LineDiscountPopover({ discount, onChange, onClose }: Props) {
  const [type, setType] = useState<DiscountType>(discount.type)
  const [value, setValue] = useState(discount.value ? String(discount.value) : '')
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    function onDocClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) onClose()
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('mousedown', onDocClick)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDocClick)
      document.removeEventListener('keydown', onKey)
    }
  }, [onClose])

  const apply = () => {
    if (type === 'None') {
      onChange({ type: 'None', value: 0 })
    } else {
      onChange({ type, value: Math.max(0, Number(value) || 0) })
    }
    onClose()
  }

  return (
    <div
      ref={ref}
      className="absolute right-0 top-8 z-20 w-56 space-y-2 rounded-xl border border-border bg-surface p-3 shadow-card-lg"
    >
      <Select
        aria-label="Discount type"
        value={type}
        onChange={(e) => setType(e.target.value as DiscountType)}
      >
        <option value="None">No discount</option>
        <option value="Percentage">% off</option>
        <option value="FixedAmount">₱ off</option>
      </Select>
      {type !== 'None' && (
        <input
          type="number"
          min={0}
          step={type === 'Percentage' ? '1' : '0.01'}
          value={value}
          onChange={(e) => setValue(e.target.value)}
          placeholder={type === 'Percentage' ? '0–100' : 'Amount'}
          aria-label="Discount value"
          className={cn(inputClass, 'h-9')}
        />
      )}
      <Button block size="sm" onClick={apply}>
        Apply
      </Button>
    </div>
  )
}
