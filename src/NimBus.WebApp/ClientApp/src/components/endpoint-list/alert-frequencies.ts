// The frequency windows an alert subscription can pick, in seconds. A subscriber is
// notified at most once per window. The short windows suit a webhook that starts an
// automation: a burst of failures still sends one request.
export const ALERT_FREQUENCIES: readonly { value: string; label: string }[] = [
  { value: "300", label: "Every 5 minutes" },
  { value: "900", label: "Every 15 minutes" },
  { value: "3600", label: "Hourly" },
  { value: "86400", label: "Daily" },
  { value: "604800", label: "Weekly" },
];

export const DEFAULT_ALERT_FREQUENCY = "86400";
