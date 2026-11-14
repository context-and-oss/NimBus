import { useEffect, useRef, useState } from 'react';
import { api } from '../../api';
import { formatDelay, useErpDemoState } from '../../demo-state';
import { PaneSection, SwitchButton } from '../shell/ui';

// Demo control for the ERP adapter's artificial per-message processing delay. The adapter's
// ProcessingDelayMiddleware polls /api/admin/processing-delay and, when enabled, holds each
// inbound message for `delayMs` before its handler runs, so processing visibly takes time on
// the Flow monitor.

const DEBOUNCE_MS = 300;
const MIN_MS = 100;
const MAX_MS = 100_000;

export default function ProcessingDelayPanel() {
  const { refresh } = useErpDemoState();
  const [enabled, setEnabled] = useState<boolean>(false);
  const [delayMs, setDelayMs] = useState<number>(2000);
  const [loaded, setLoaded] = useState<boolean>(false);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const skipNextSendRef = useRef<boolean>(true);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const m = await api.getProcessingDelay();
        if (cancelled) return;
        setEnabled(m.enabled);
        setDelayMs(Math.max(MIN_MS, Math.min(MAX_MS, Math.round(m.delayMs))));
        setLoaded(true);
      } catch {
        setLoaded(true);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    if (!loaded) return;
    // First render after the GET seeds initial values; don't echo it back.
    if (skipNextSendRef.current) {
      skipNextSendRef.current = false;
      return;
    }
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      api.setProcessingDelay({ enabled, delayMs })
        .then(refresh)
        .catch(() => { /* ignore — UI will reflect next refresh */ });
    }, DEBOUNCE_MS);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [enabled, delayMs, loaded, refresh]);

  return (
    <PaneSection
      title="Processing delay"
      description="The ERP adapter holds each inbound message for this long before handling it, like a slow consumer."
    >
      <SwitchButton
        on={enabled}
        disabled={!loaded}
        onClick={() => setEnabled((v) => !v)}
        label={`Processing delay: ${enabled ? 'ON' : 'OFF'}`}
        title="When ON, every inbound ERP message waits the configured delay before its handler runs."
      />
      <div className="flex flex-col gap-1">
        <div className="flex items-center justify-between text-xs text-ink-muted">
          <label htmlFor="processing-delay">Delay per message</label>
          <span className="font-mono tabular-nums text-ink">{formatDelay(delayMs)}</span>
        </div>
        <input
          id="processing-delay"
          type="range"
          min={MIN_MS}
          max={MAX_MS}
          step={100}
          value={delayMs}
          onChange={(e) => setDelayMs(Number(e.target.value))}
          disabled={!loaded}
          className="w-full accent-accent"
        />
        <div className="flex justify-between font-mono text-[10px] text-ink-muted">
          <span>100 ms</span>
          <span>100 s</span>
        </div>
      </div>
    </PaneSection>
  );
}
