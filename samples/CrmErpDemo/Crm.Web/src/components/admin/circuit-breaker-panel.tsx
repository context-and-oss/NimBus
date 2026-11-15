import { useCrmDemoState } from '../../demo-state';
import { Chip, PaneSection, SwitchButton, type Tone } from '../shell/ui';

// Circuit-breaker showcase: the "CRM API outage" switch makes every CRM adapter call fail with
// 503, the failure rate opens the CrmEndpoint circuit, and the NimBus receiver pauses instead
// of burning retries into the dead-letter queue. The chip shows the circuit live.

const STATES: Record<string, { tone: Tone; label: string; hint: string; pulse: boolean }> = {
  Closed: { tone: 'ok', label: 'Circuit closed', hint: 'Receiving normally.', pulse: false },
  Open: {
    tone: 'bad',
    label: 'Circuit open',
    hint: 'Receiver paused. Messages wait on the subscription and no retries are spent.',
    pulse: true,
  },
  HalfOpen: {
    tone: 'warn',
    label: 'Circuit half-open',
    hint: 'Probing with one session. Successes close the circuit.',
    pulse: true,
  },
};

export default function CircuitBreakerPanel() {
  const { outage, circuit, setOutage } = useCrmDemoState();
  const enabled = outage ?? false;
  const state = STATES[circuit?.state ?? 'Closed'] ?? STATES.Closed;

  return (
    <PaneSection
      title="Circuit breaker · CrmEndpoint"
      description="Simulate a CRM API outage: every adapter call fails with 503 until you turn it off."
    >
      <SwitchButton
        on={enabled}
        danger
        disabled={outage === null}
        onClick={() => void setOutage(!enabled)}
        label={`CRM API outage: ${enabled ? 'ON' : 'OFF'}`}
      />
      <div className="flex flex-wrap items-center gap-2">
        <Chip tone={state.tone} dot pulse={state.pulse}>{state.label}</Chip>
        <span className="text-xs text-ink-muted">{state.hint}</span>
      </div>
      {circuit?.reason && <p className="truncate font-mono text-[11px] text-ink-muted" title={circuit.reason}>{circuit.reason}</p>}
    </PaneSection>
  );
}
