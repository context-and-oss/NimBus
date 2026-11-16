import { useEffect, useRef, useState } from 'react';
import { failurePercent, useErpDemoState } from '../../demo-state';
import { PaneSection, SwitchButton, inputClass } from '../shell/ui';

// Service mode: the ERP adapter rejects every inbound message (dead-letter / resubmit flow).
// Error mode: ERP adapter handlers throw the selected failure before doing any work, so the
// NimBus WebApp's Integration Intelligence card gets a realistic failure to classify. The
// failure rate sets the share of messages that fail; "Random" picks a failure per message.

const DEBOUNCE_MS = 300;

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

  // The slider keeps its own value while it is dragged and until the debounced save lands;
  // otherwise it follows the polled state.
  const [draftPercent, setDraftPercent] = useState<number | null>(null);
  const percent = draftPercent ?? failurePercent(errorMode?.failureRate);
  const enabledRef = useRef(enabled);
  enabledRef.current = enabled;
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);

  const onRateChange = (value: number) => {
    setDraftPercent(value);
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      timer.current = null;
      void setErrorMode(enabledRef.current, undefined, value / 100).finally(() => {
        if (timer.current === null) setDraftPercent(null);
      });
    }, DEBOUNCE_MS);
  };

  return (
    <PaneSection title="Error mode">
      <SwitchButton
        on={enabled}
        danger
        disabled={errorMode === null || busy}
        onClick={() => void setErrorMode(!enabled)}
        label={errorMode === null ? 'Error mode' : `Error mode: ${enabled ? 'ON' : 'OFF'}`}
        title="When ON, ERP adapter message handlers throw the selected failure for the chosen share of messages."
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
      <div className="flex flex-col gap-1">
        <div className="flex items-center justify-between text-xs text-ink-muted">
          <label htmlFor="error-mode-rate">Failure rate</label>
          <span className="font-mono tabular-nums text-ink">{percent}% of messages fail</span>
        </div>
        <input
          id="error-mode-rate"
          type="range"
          min={0}
          max={100}
          step={1}
          value={percent}
          onChange={(e) => onRateChange(Number(e.target.value))}
          disabled={errorMode === null}
          className="w-full accent-[#c50f1f]"
        />
      </div>
      <p className="text-xs leading-relaxed text-ink-muted">
        {selected
          ? <>{selected.description} NimBus outcome: <strong className="text-ink">{selected.disposition}</strong>.</>
          : 'Which failure the ERP handlers throw while error mode is on.'}
        {percent < 100 && ' Each message rolls on its own, and a resubmit rolls again.'}
      </p>
    </PaneSection>
  );
}
