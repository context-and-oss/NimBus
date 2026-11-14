import { useState } from 'react';
import { api } from '../../api';
import { PaneSection } from '../shell/ui';

// Demo reset: deletes every CRM business row (accounts, contacts, audit) plus the NimBus
// inbox-dedup rows in the CRM database. Irreversible; publishes no delete events, so NimBus
// message history and the ERP side are untouched.

export default function DataResetPanel() {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<string | null>(null);

  const wipe = async () => {
    const confirmed = window.confirm(
      'Delete ALL CRM data?\n\n' +
        'Removes every account, contact and audit row (and NimBus inbox rows in this database). ' +
        'No delete events are published — NimBus message history and the ERP side stay as they are.\n\n' +
        'This cannot be undone.',
    );
    if (!confirmed) return;
    setBusy(true);
    setResult(null);
    try {
      const r = await api.deleteAllData();
      setResult(`Deleted ${r.accounts} accounts · ${r.contacts} contacts · ${r.audits} audit rows`);
    } catch (err) {
      setResult(`Reset failed: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <PaneSection
      title={<span className="text-[#a4262c]">Danger zone</span>}
      description="Wipes every CRM account, contact and audit row for a clean demo slate. Irreversible. No delete events are published."
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
