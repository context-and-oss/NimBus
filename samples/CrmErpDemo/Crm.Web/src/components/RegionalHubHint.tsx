import { useEffect, useState } from 'react';
import { RegionalHub, api } from '../api';

// Routing-keys showcase: the account's country is the routing key of CrmAccountCreated and
// CrmAccountUpdated, and each regional hub endpoint takes only its own countries. This line
// says, while you type, where the account will go. The hub list comes from crm-api, which
// reads it from the endpoint declarations the Service Bus topology is built from.

export default function RegionalHubHint({ countryCode }: { countryCode?: string | null }) {
  const [hubs, setHubs] = useState<RegionalHub[]>([]);

  useEffect(() => {
    let cancelled = false;
    api.getRegionalHubs()
      .then(all => { if (!cancelled) setHubs(all); })
      .catch(() => { /* crm-api unreachable: show no hint rather than a guess */ });
    return () => {
      cancelled = true;
    };
  }, []);

  const country = countryCode ?? '';
  if (hubs.length === 0 || country.length !== 2) return null;

  const hub = hubs.find(h => h.countries.includes(country));
  const unrouted = hubs.find(h => h.isUnrouted);

  return (
    <p className="mt-1 text-[12px] text-ink-muted" data-testid="regional-hub-hint">
      {hub ? (
        <>Routing key <code>{country}</code>: goes to the {hub.name} (<code>{hub.endpointId}</code>).</>
      ) : (
        <>
          Routing key <code>{country}</code>: no regional hub claims it, so it goes to{' '}
          <code>{unrouted?.endpointId ?? 'the unmatched consumer'}</code> and is dead-lettered there.
        </>
      )}
    </p>
  );
}
