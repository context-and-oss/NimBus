import { useErpDemoState } from '../../demo-state';
import { PaneSection, SwitchButton, inputClass } from '../shell/ui';

// Service mode: the ERP adapter rejects every inbound message (dead-letter / resubmit flow).
// Error mode: every ERP adapter handler throws the selected failure before doing any work, so
// the NimBus WebApp's Integration Intelligence card gets a realistic failure to classify.

export function ServiceModeSection() {
  const { serviceMode, busy, setServiceMode } = useErpDemoState();
  const enabled = serviceMode?.enabled ?? false;
  return (
    <PaneSection title="Service mode" description="The ERP adapter rejects every inbound message, which shows the dead-letter and resubmit flow.">
      <SwitchButton
        on={enabled}
        danger
        disabled={serviceMode === null || busy}
        onClick={() => void setServiceMode(!enabled)}
        label={serviceMode === null ? 'Service mode' : `Service mode: ${enabled ? 'ON' : 'OFF'}`}
        title="When ON, the ERP adapter rejects every inbound message — useful for showing the dead-letter / resubmit flow."
      />
    </PaneSection>
  );
}

export function ErrorModeSection() {
  const { errorMode, reasons, busy, setErrorMode } = useErpDemoState();
  const enabled = errorMode?.enabled ?? false;
  const selected = reasons.find((r) => r.id === errorMode?.reason);
  return (
    <PaneSection title="Error mode">
      <SwitchButton
        on={enabled}
        danger
        disabled={errorMode === null || busy}
        onClick={() => void setErrorMode(!enabled)}
        label={errorMode === null ? 'Error mode' : `Error mode: ${enabled ? 'ON' : 'OFF'}`}
        title="When ON, every ERP adapter message handler throws the selected failure."
      />
      <select
        aria-label="Failure reason"
        value={errorMode?.reason ?? ''}
        onChange={(e) => void setErrorMode(enabled, e.target.value)}
        disabled={errorMode === null || busy || reasons.length === 0}
        className={inputClass}
      >
        {reasons.length === 0 && <option value="">Loading reasons…</option>}
        {reasons.map((r) => <option key={r.id} value={r.id}>{r.title}</option>)}
      </select>
      <p className="text-xs leading-relaxed text-ink-muted">
        {selected
          ? <>{selected.description} NimBus outcome: <strong className="text-ink">{selected.disposition}</strong>.</>
          : 'Which failure the ERP handlers throw while error mode is on.'}
      </p>
    </PaneSection>
  );
}
