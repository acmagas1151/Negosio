import { ReceiptBusinessInfo } from './ReceiptBusinessInfo'

interface ReceiptHeaderProps {
  headerText: string | null
  business: React.ComponentProps<typeof ReceiptBusinessInfo>
}

export function ReceiptHeader({ headerText, business }: ReceiptHeaderProps) {
  if (headerText && headerText.trim()) {
    return (
      <>
        {headerText.split('\n').map((line, i) => (
          <p key={i} className="center bold">
            {line || ' '}
          </p>
        ))}
      </>
    )
  }

  return <ReceiptBusinessInfo {...business} />
}
