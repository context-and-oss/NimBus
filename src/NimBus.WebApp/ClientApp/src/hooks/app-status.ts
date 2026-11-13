import { useEffect, useState } from "react";
// Type-only import: the api-client module (345KB, pulls in moment) must not
// land in the eager entry chunk — the runtime module is loaded on demand
// inside getApplicationStatus via dynamic import.
import type * as api from "api-client";

// Module-level cache for static app status (never changes during runtime)
let cachedStatus: api.ApplicationStatus | null = null;
let pendingRequest: Promise<api.ApplicationStatus> | null = null;

export const useEnv = () => {
  const [result, setResult] = useState<string | undefined>(undefined);
  useEffect(() => {
    const fetchData = async () => {
      const data = await getApplicationStatus();
      setResult(data?.env);
    };
    fetchData().catch(console.error);
  }, []);
  return result;
};

export const getApplicationStatus = async () => {
  // Return cached value if available
  if (cachedStatus) {
    return cachedStatus;
  }

  // Deduplicate concurrent requests - return existing promise
  if (pendingRequest) {
    return pendingRequest;
  }

  // Make single request and cache result. The async IIFE is assigned to
  // pendingRequest synchronously (before any await), so concurrent callers
  // still dedupe onto the same in-flight promise while the api-client module
  // itself loads lazily. pendingRequest is cleared in finally — success AND
  // failure — so a transient fetch error doesn't pin a rejected promise as
  // the answer for the rest of the page's lifetime; the next call retries.
  pendingRequest = (async () => {
    try {
      const apiMod = await import("api-client");
      const client = new apiMod.Client(apiMod.CookieAuth());
      const status = await client.getApiAppStats();
      // GH#93: /api/app/stats returns an EMPTY status object to unauthenticated
      // callers. Caching that would pin blank badges for the page's lifetime, so
      // only a populated answer is cached — the next call then retries. For an
      // authenticated caller these are always set, so no real cache is lost.
      if (status?.env || status?.platformVersion || status?.storageProvider) {
        cachedStatus = status;
      }
      return status;
    } finally {
      pendingRequest = null;
    }
  })();

  return pendingRequest;
};

/* export const usePlatformVersion = () => {
  const data = useAsyncMemo(async () => {
    return getApplicationStatus()
  }, [])

  return data?.platformVersion;
};*/

export const usePlatformVersion = () => {
  const [result, setResult] = useState<string | undefined>(undefined);
  useEffect(() => {
    const fetchData = async () => {
      const data = await getApplicationStatus();
      setResult(data?.platformVersion);
    };
    fetchData().catch(console.error);
  }, []);
  return result;
};

export const useNimBusVersion = () => {
  const [result, setResult] = useState<string | undefined>(undefined);
  useEffect(() => {
    const fetchData = async () => {
      const data = await getApplicationStatus();
      setResult(data?.nimbusVersion ?? undefined);
    };
    fetchData().catch(console.error);
  }, []);
  return result;
};

/**
 * The platform catalog package that defines the endpoints and event types, as
 * "<name> <version>" (e.g. "EET.Platform 1.0.1"); undefined until the status has
 * loaded or for an anonymous caller.
 */
export const usePlatformPackage = () => {
  const [result, setResult] = useState<string | undefined>(undefined);
  useEffect(() => {
    const fetchData = async () => {
      const data = await getApplicationStatus();
      setResult(formatPlatformPackage(data));
    };
    fetchData().catch(console.error);
  }, []);
  return result;
};

export const formatPlatformPackage = (
  status: api.ApplicationStatus | null | undefined,
): string | undefined => {
  if (!status?.platformName || !status?.platformVersion) return undefined;
  return `${status.platformName} ${status.platformVersion}`;
};

export const useStorageProvider = () => {
  const [result, setResult] = useState<string | undefined>(undefined);
  useEffect(() => {
    const fetchData = async () => {
      const data = await getApplicationStatus();
      setResult(data?.storageProvider);
    };
    fetchData().catch(console.error);
  }, []);
  return result;
};

// "{ticket}" placeholder URL template for reported-event deep links; undefined
// or empty when no ticket system is configured (the Reported column then
// renders a plain badge without a link).
/**
 * The ticket link for one endpoint (Spec 044 Phase 3): its own template from its ownership
 * metadata when an Owner set one, else the site's. Undefined while loading or when neither is set.
 */
export const useEndpointTicketLinkTemplate = (endpointId: string | undefined) => {
  const site = useTicketLinkTemplate();
  const [own, setOwn] = useState<string | undefined>(undefined);
  useEffect(() => {
    if (!endpointId) return;
    let active = true;
    import("api-client")
      .then((apiMod) => new apiMod.Client(apiMod.CookieAuth()).getMetadataEndpoint(endpointId))
      .then((metadata) => {
        if (active) setOwn(metadata?.ticketLinkTemplate ?? undefined);
      })
      // No metadata recorded (404) or not readable: the site's template applies.
      .catch(() => undefined);
    return () => {
      active = false;
    };
  }, [endpointId]);
  return own ?? site;
};

export const useTicketLinkTemplate = () => {
  const [result, setResult] = useState<string | undefined>(undefined);
  useEffect(() => {
    const fetchData = async () => {
      const data = await getApplicationStatus();
      setResult(data?.ticketLinkTemplate ?? undefined);
    };
    fetchData().catch(console.error);
  }, []);
  return result;
};
