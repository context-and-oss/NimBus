import type { APIRequestContext, APIResponse } from "@playwright/test";
import { request } from "@playwright/test";
import { ServiceUrls } from "./service-urls.js";

/**
 * "<status> <text>" for a failed call. Since NimBus 4.12.0 every /api error is an RFC 7807
 * problem response, so the text a test matches on is in its `detail`. A body that is not a
 * problem is reported as it is.
 */
export async function describeFailure(res: APIResponse): Promise<string> {
  const text = await res.text();
  try {
    const problem = JSON.parse(text) as { detail?: unknown; title?: unknown };
    const reason = typeof problem.detail === "string" ? problem.detail
      : typeof problem.title === "string" ? problem.title : text;
    return `${res.status()} ${reason}`;
  } catch {
    return `${res.status()} ${text}`;
  }
}

export interface EndpointStatusCount {
  endpointId: string;
  subscriptionStatus: string;
  eventTime: string;
  failedCount: number;
  deferredCount: number;
  pendingCount: number;
  unsupportedCount: number;
  deadletterCount: number;
}

export interface MessageErrorContent {
  errorText?: string | null;
  errorType?: string | null;
  [key: string]: unknown;
}

export interface MessageContent {
  errorContent?: MessageErrorContent | null;
  [key: string]: unknown;
}

export interface NimBusEvent {
  eventId: string;
  sessionId: string;
  endpointId: string;
  eventTypeId: string;
  resolutionStatus: string;
  lastMessageId: string;
  pendingSubStatus?: string | null;
  handoffReason?: string | null;
  externalJobId?: string | null;
  expectedBy?: string | null;
  /** Search results nest the error under messageContent.errorContent. */
  messageContent?: MessageContent | null;
  // ... many more fields, but tests only need the above
  [key: string]: unknown;
}

export interface SearchResponse {
  events: NimBusEvent[];
  continuationToken: string | null;
}

export interface EventFilter {
  endPointId?: string | null;
  eventId?: string | null;
  sessionId?: string | null;
  resolutionStatus?: string[];
  eventTypeId?: string[];
  to?: string | null;
  from?: string | null;
  payload?: string | null;
  // dates omitted for brevity
}

/**
 * REST-only client for the NimBus management WebApp. Tests use this for
 * assertions and bulk setup; the resubmit-via-UI flow lives in the spec
 * file itself so the test still demonstrates the operator's actual workflow.
 */
export class NimBusApiClient {
  private constructor(private readonly api: APIRequestContext) {}

  static async create(): Promise<NimBusApiClient> {
    const api = await request.newContext({
      baseURL: ServiceUrls.nimbusOps,
      ignoreHTTPSErrors: true,
      extraHTTPHeaders: { "api-version": "2" },
    });
    return new NimBusApiClient(api);
  }

  async dispose(): Promise<void> {
    await this.api.dispose();
  }

  /** Bulk endpoint state counts. Pass the endpoint IDs you care about. */
  async getStatusCounts(endpointIds: string[]): Promise<EndpointStatusCount[]> {
    const res = await this.api.post("/api/v1/endpoint/status/count", { data: endpointIds });
    if (!res.ok()) throw new Error(`NimBus POST status/count → ${await describeFailure(res)}`);
    return (await res.json()) as EndpointStatusCount[];
  }

  /** Get a single endpoint's state count. */
  async getStatusCount(endpointId: string): Promise<EndpointStatusCount> {
    const res = await this.api.get(`/api/v1/endpoint/${endpointId}/status/count`);
    if (!res.ok()) throw new Error(`NimBus GET ${endpointId}/status/count → ${res.status()}`);
    return (await res.json()) as EndpointStatusCount;
  }

  /** Search messages on an endpoint by filter (status, event id, session id, etc.). */
  async searchEvents(endpointId: string, filter: EventFilter, max = 100): Promise<NimBusEvent[]> {
    const res = await this.api.post(`/api/v1/event/${endpointId}/getByFilter`, {
      data: {
        continuationToken: "",
        eventFilter: filter,
        maxSearchItemsCount: max,
      },
    });
    if (!res.ok()) throw new Error(`NimBus search → ${await describeFailure(res)}`);
    const body = (await res.json()) as SearchResponse;
    return body.events ?? [];
  }

  /** Resubmit a single failed event by (eventId, lastMessageId). */
  async resubmit(eventId: string, messageId: string): Promise<void> {
    const res = await this.api.post(`/api/v1/event/resubmit/${eventId}/${messageId}`);
    if (!res.ok()) throw new Error(`NimBus resubmit → ${await describeFailure(res)}`);
  }

  /** Skip a single event (terminal — flips Failed → Skipped, unblocks the session). */
  async skipMessage(eventId: string, messageId: string): Promise<void> {
    const res = await this.api.post(`/api/v1/event/skip/${eventId}/${messageId}`);
    if (!res.ok()) throw new Error(`NimBus skip → ${await describeFailure(res)}`);
  }

  async settleHandoff(endpoint: string, event: NimBusEvent, outcome: "complete" | "fail", reason = "E2E external rejection"): Promise<void> {
    const res = await this.api.post(`/api/v1/event/handoff/${outcome}/${endpoint}/${event.eventId}/${event.lastMessageId}`,
      { data: outcome === "fail" ? { reason } : {} });
    if (!res.ok()) throw new Error(`NimBus handoff ${outcome} → ${await describeFailure(res)}`);
  }

  async channelState(endpoint: string, channel: "send" | "subscription"): Promise<string> {
    const res = await this.api.get(`/api/v1/endpoint/${endpoint}/${channel}status`);
    if (!res.ok()) throw new Error(`NimBus channel status: ${res.status()}`);
    return await res.text();
  }

  async history(endpoint: string, eventId: string): Promise<Array<{ messageType: string; [key: string]: unknown }>> {
    const res = await this.api.get(`/api/v1/event/details/${endpoint}/${eventId}/history`);
    if (!res.ok()) throw new Error(`NimBus event history: ${res.status()}`);
    return await res.json();
  }

  async setChannelState(endpoint: string, channel: "send" | "subscription", enabled: boolean): Promise<void> {
    const res = await this.api.post(`/api/v1/endpoint/${endpoint}/${channel}status`, {
      data: JSON.stringify(enabled ? "enable" : "disable"), headers: { "Content-Type": "application/json" },
    });
    if (!res.ok()) throw new Error(`NimBus change channel status: ${await describeFailure(res)}`);
  }

  /** Trigger reprocessing of all Deferred messages on a (endpoint, session). */
  async reprocessDeferred(endpointId: string, sessionId: string): Promise<void> {
    const res = await this.api.post(`/api/v1/event/reprocess-deferred/${endpointId}/${sessionId}`);
    if (!res.ok()) throw new Error(`NimBus reprocess-deferred → ${await describeFailure(res)}`);
  }
}
