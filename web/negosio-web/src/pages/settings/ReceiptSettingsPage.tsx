import { DashboardLayout } from '../../components/layout/DashboardLayout'
import { SettingsTabs } from '../../components/settings/SettingsTabs'
import { Card, PageHeader } from '../../components/ui'

export default function ReceiptSettingsPage() {
  return (
    <DashboardLayout title="Receipt settings">
      <div className="space-y-6">
        <PageHeader
          title="Receipt settings"
          description="Customize the information and appearance shown on printed receipts."
        />
        <SettingsTabs />
        <Card padding="lg" className="max-w-xl">
          <p className="text-sm text-text-muted">
            Receipt configuration form — coming in the next step.
          </p>
        </Card>
      </div>
    </DashboardLayout>
  )
}
