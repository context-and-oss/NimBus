import AlertsPanel, { useAlerts } from '../components/admin/alerts-panel';
import { ViewHeader } from '../components/shell/ui';

export default function AlertsPage() {
  const { alerts, clear } = useAlerts();
  return (
    <>
      <ViewHeader title="Notification alerts" count={alerts.length} />
      <div className="max-w-4xl px-5 pb-5">
        <AlertsPanel alerts={alerts} clear={clear} />
      </div>
    </>
  );
}
