import {
  Area,
  AreaChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import type { SalesTrendPointDto } from '../../api/types'
import { formatMoney } from '../../lib/format'
import { EmptyState } from '../ui'
import { TrendingUp } from 'lucide-react'

interface Props {
  points: SalesTrendPointDto[]
}

function TrendTooltip({
  active,
  payload,
}: {
  active?: boolean
  payload?: { payload: SalesTrendPointDto }[]
}) {
  if (!active || !payload?.length) return null
  const point = payload[0].payload
  return (
    <div className="rounded-lg border border-border bg-surface px-3 py-2 text-[13px] shadow-card-lg">
      <p className="font-semibold text-text-primary">{point.bucketLabel}</p>
      <p className="text-text-secondary">{formatMoney(point.netSales)}</p>
      <p className="text-text-muted">
        {point.transactions} transaction{point.transactions === 1 ? '' : 's'}
      </p>
    </div>
  )
}

/** Net sales over the selected range — no unnecessary animation, tooltip shows the exact figure. */
export function SalesTrendChart({ points }: Props) {
  const hasData = points.some((p) => p.netSales > 0)

  if (!hasData) {
    return (
      <EmptyState
        icon={TrendingUp}
        title="No sales in this range"
        description="Try a wider date range or different filters."
      />
    )
  }

  return (
    <ResponsiveContainer width="100%" height={280}>
      <AreaChart data={points} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
        <defs>
          <linearGradient id="salesTrendFill" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--color-primary-500)" stopOpacity={0.22} />
            <stop offset="100%" stopColor="var(--color-primary-500)" stopOpacity={0} />
          </linearGradient>
        </defs>
        <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="var(--color-border)" />
        <XAxis
          dataKey="bucketLabel"
          tick={{ fontSize: 12, fill: 'var(--color-text-muted)' }}
          tickLine={false}
          axisLine={{ stroke: 'var(--color-border)' }}
          interval="preserveStartEnd"
          minTickGap={24}
        />
        <YAxis
          tick={{ fontSize: 12, fill: 'var(--color-text-muted)' }}
          tickLine={false}
          axisLine={false}
          width={56}
          tickFormatter={(v: number) => (v >= 1000 ? `₱${Math.round(v / 1000)}k` : `₱${v}`)}
        />
        <Tooltip content={<TrendTooltip />} isAnimationActive={false} />
        <Area
          type="monotone"
          dataKey="netSales"
          stroke="var(--color-primary-600)"
          strokeWidth={2}
          fill="url(#salesTrendFill)"
          isAnimationActive={false}
          dot={false}
          activeDot={{ r: 4 }}
        />
      </AreaChart>
    </ResponsiveContainer>
  )
}
