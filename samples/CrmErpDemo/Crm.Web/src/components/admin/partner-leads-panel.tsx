import { useEffect, useState } from 'react';
import { api } from '../../api';
import { PaneSection, SwitchButton } from '../shell/ui';

// CloudEvents partner showcase: partner-portal publishes a PartnerLeadSubmitted lead every 45
// seconds, which is too chatty for a demo left running. The switch lives in crm-api (in memory,
// like the outage toggle) and partner-portal reads it before each lead, so a flip takes effect
// within seconds and needs no restart.

export default function PartnerLeadsPanel() {
  const [enabled, setEnabled] = useState<boolean>(true);
  const [loaded, setLoaded] = useState<boolean>(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const m = await api.getPartnerLeads();
        if (!cancelled) {
          setEnabled(m.enabled);
          setLoaded(true);
        }
      } catch {
        /* crm-api unreachable: the switch stays disabled rather than showing a guess */
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const toggle = async () => {
    try {
      setEnabled((await api.setPartnerLeads(!enabled)).enabled);
    } catch {
      /* the next click reconciles */
    }
  };

  return (
    <PaneSection
      title="Partner leads · PartnerPortal"
      description="An external partner sends a lead as a raw CloudEvent every 45 seconds. Each one arrives as a contact with a Partner badge."
    >
      <SwitchButton
        on={enabled}
        disabled={!loaded}
        onClick={() => void toggle()}
        label={`Partner leads: ${!loaded ? '…' : enabled ? 'ON' : 'OFF'}`}
      />
    </PaneSection>
  );
}
