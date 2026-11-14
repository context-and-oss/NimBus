import { useState } from 'react';
import { api } from '../../api';
import { PaneSection } from '../shell/ui';

// Demo reset: deletes every ERP business row (customers, contacts, audit) plus the NimBus
// outbox rows in the ERP database; pending outbox rows would otherwise publish ghost events
// about entities that no longer exist. Irreversible; publishes no delete events, so NimBus
// message history and the CRM side are untouched.

export default function DataResetPanel() {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<string | null>(null);

  const wipe = async () => {
    const confirmed = window.confirm(
      'Delete ALL ERP data?\n\n' +
        'Removes every customer, contact and audit row, and clears pending NimBus outbox rows in this database. ' +
        'No delete events are published — NimBus message history and the CRM side stay as they are.\n\n' +
        'This cannot be undone.',
    );
    if (!confirmed) return;
    setBusy(true);
    setResult(null);
    try {
      const r = await api.deleteAllData();
      setResult(
        `Deleted ${r.customers} customers · ${r.contacts} contacts · ${r.audits} audit rows · ${r.outboxRows} outbox rows`,
      );
    } catch (err) {
      setResult(`Reset failed: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <PaneSection
      title={<span className="text-[#a4262c]">Danger zone</span>}
      description="Wipes every ERP customer, contact and audit row, plus pending outbox rows, for a clean demo slate. Irreversible. No delete events are published."
    >
      <button
        type="button"
        onClick={() => void wipe()}
        disabled={busy}
        className="self-start rounded border border-[#a4262c] px-3 py-1 text-[13px] font-semibold text-[#a4262c] hover:bg-[#fde7e9] disabled:opacity-50"
      >
        {busy ? 'Deleting…' : 'Delete all data'}
      </button>
      {result && <p className="font-mono text-[11px] text-ink-muted">{result}</p>}
    </PaneSection>
  );
}
