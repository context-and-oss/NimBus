import type { ConsumerRouting, SubscriptionRuleRouting } from "api-client";

/**
 * Spec 045: a short label for how a consumer takes an event type — its routing keys, or
 * "unmatched" for the consumer of keys nobody claims. Undefined for a consumer that takes
 * every key, which shows no label at all.
 */
export function routingLabel(routing: ConsumerRouting | undefined): string | undefined {
  if (!routing) return undefined;
  if (routing.unmatched) return "unmatched";
  if (routing.routingKeys && routing.routingKeys.length > 0) {
    return `${routing.routingKeys.length === 1 ? "key" : "keys"} ${routing.routingKeys.join(", ")}`;
  }
  return undefined;
}

/** Routing labels keyed by consumer endpoint name, for the consumers that have one. */
export function routingLabels(
  routings: ConsumerRouting[] | undefined,
): Record<string, string> {
  const labels: Record<string, string> = {};
  for (const routing of routings ?? []) {
    const label = routingLabel(routing);
    if (routing.endpoint && label) labels[routing.endpoint] = label;
  }
  return labels;
}

/**
 * Spec 045: a short label for the routing-key filter of a live forward rule — the keys a
 * keyed rule takes, or the keys the unmatched rule excludes. Rules that take every key
 * carry no routing entry and show no label.
 */
export function ruleRoutingLabel(routing: SubscriptionRuleRouting): string {
  const keys = (routing.routingKeys ?? []).join(", ");
  if (routing.unmatched) return keys ? `unmatched · not ${keys}` : "unmatched";
  return `${routing.routingKeys?.length === 1 ? "key" : "keys"} ${keys}`;
}
