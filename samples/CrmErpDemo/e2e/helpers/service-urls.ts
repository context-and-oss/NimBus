// Centralized URL resolution. Tests pull these once at startup and pass to clients.

function required(name: string, fallback?: string): string {
  const value = process.env[name] ?? fallback;
  if (!value) {
    throw new Error(
      `Missing environment variable ${name}. Copy .env.example to .env.local and fill in the URL Aspire assigned to this service (visible in the Aspire dashboard).`,
    );
  }
  return value.replace(/\/$/, "");
}

export const ServiceUrls = {
  crmApi: required("CRM_API_URL", "http://localhost:5080"),
  erpApi: required("ERP_API_URL", "http://localhost:5090"),
  nimbusOps: required("NIMBUS_OPS_URL", "http://localhost:18080"),
  /** Mailpit's web API: the local AppHost points the Resolver's alert mail at it. */
  mailpit: required("MAILPIT_URL", "http://localhost:8025"),
};

export const Timeouts = {
  propagationMs: Number(process.env.PROPAGATION_TIMEOUT_MS ?? 60_000),
  failedMessageMs: Number(process.env.FAILED_MESSAGE_TIMEOUT_MS ?? 45_000),
};
