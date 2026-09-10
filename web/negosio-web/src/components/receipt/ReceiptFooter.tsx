interface ReceiptFooterProps {
  footerText: string | null
}

export function ReceiptFooter({ footerText }: ReceiptFooterProps) {
  if (footerText && footerText.trim()) {
    return (
      <>
        {footerText.split('\n').map((line, i) => (
          <p key={i} className="center muted">
            {line || ' '}
          </p>
        ))}
      </>
    )
  }

  return <p className="center muted">Thank you!</p>
}
