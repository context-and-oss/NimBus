import { MAX_RATE_PER_MINUTE, MIN_RATE_PER_MINUTE, SLOPPY_COUNTRY, simulator, useSimulator } from '../../simulator';
import { PaneSection, SwitchButton } from '../shell/ui';

// Auto-generates account and contact activity in this browser to drive Service Bus traffic.

export default function SimulatorPanel() {
  const sim = useSimulator();
  return (
    <PaneSection
      title="Simulated activity"
      description="Creates, updates and deletes accounts and contacts from this browser to keep messages flowing."
    >
      <SwitchButton
        on={sim.running}
        onClick={() => simulator.toggle()}
        label={sim.running ? `Simulating · ${sim.actions}` : 'Simulate'}
        title="Auto-generate account & contact activity to drive Service Bus traffic"
      />
      <label className="flex flex-col gap-1 text-xs text-ink-muted" title="Target simulated actions per minute; applies live while simulating">
        <span className="flex justify-between">
          <span>Rate</span>
          <span className="font-mono tabular-nums text-ink">{sim.ratePerMinute}/min</span>
        </span>
        <input
          type="range"
          min={MIN_RATE_PER_MINUTE}
          max={MAX_RATE_PER_MINUTE}
          step={10}
          value={sim.ratePerMinute}
          onChange={(e) => simulator.setRate(Number(e.target.value))}
          className="w-full accent-accent"
          aria-label="Simulated messages per minute"
        />
      </label>
      <SwitchButton
        on={sim.sloppy}
        onClick={() => simulator.setSloppy(!sim.sloppy)}
        label={sim.sloppy ? 'Sloppy data entry: ON' : 'Sloppy data entry'}
        title={`About one in five simulated accounts gets the country "${SLOPPY_COUNTRY}", which is not an ISO code. CRM publishes it as typed and ERP rejects it.`}
      />
      {sim.sloppy ? (
        <p className="text-xs text-ink-muted">
          About one in five simulated accounts is saved with country <code className="font-mono">{SLOPPY_COUNTRY}</code>. CRM
          publishes it as typed; ERP rejects it, and the message fails on ErpEndpoint with the value quoted.
        </p>
      ) : null}
    </PaneSection>
  );
}
