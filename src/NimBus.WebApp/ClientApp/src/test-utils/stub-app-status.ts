// Test-only helpers for driving the app-status hooks against the REAL generated
// api-client. The generated Client resolves its transport as
// `this.http = http ? http : window as any` (api-client/index.ts), so stubbing
// window.fetch exercises the whole real chain — Client, CookieAuth and
// ApplicationStatus.fromJS — with no module mock (a bare vi.mock("api-client")
// is hoisted and would replace the very DTO these tests need to verify).
import { act } from "@testing-library/react";
import { vi } from "vitest";

/** An authenticated /api/app/stats body: every field populated. */
export const FULL_STATUS = {
  env: "ProdCanary",
  nimbusVersion: "3.2.1",
  platformName: "EET.Platform",
  platformVersion: "1.4.2",
  storageProvider: "Cosmos DB",
  ticketLinkTemplate: "https://tickets.example.com/{ticket}",
};

/**
 * The anonymous /api/app/stats body, byte-faithful to what the server emits:
 * System.Text.Json serializes the nulled properties of an empty ApplicationStatus
 * as explicit nulls, plus the DTO's [JsonExtensionData] bag.
 */
export const EMPTY_STATUS = {
  env: null,
  nimbusVersion: null,
  platformName: null,
  platformVersion: null,
  storageProvider: null,
  ticketLinkTemplate: null,
  additionalProperties: {},
};

/**
 * Stub window.fetch with a status body chosen per call.
 *
 * `body` is a thunk and the Response is constructed inside the implementation
 * for two reasons: a test can change what the NEXT call returns, and
 * processGetApiAppStats consumes the body with response.text() — a Response
 * body is single-read, so a shared mockResolvedValue(new Response(...)) makes
 * the second call throw "Body is unusable".
 *
 * Requests to any other URL get `{}` so unrelated fetches in a component render
 * (e.g. SidebarUserFooter's /api/auth/me) never receive a status body.
 */
export const stubStatusFetch = (body: () => unknown) => {
  const mock = vi.fn((url: RequestInfo | URL) =>
    Promise.resolve(
      new Response(
        JSON.stringify(String(url).includes("/api/v1/app/stats") ? body() : {}),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    ),
  );
  vi.stubGlobal("fetch", mock);
  // The generated Client reads `window`, not the free `fetch` binding.
  window.fetch = mock as unknown as typeof fetch;
  return mock;
};

/**
 * Explicit completion signal — do NOT poll for a hook's value instead. Every
 * app-status hook starts at `undefined`, so waitFor(() => expect(x).toBeUndefined())
 * succeeds on the first tick, before the dynamic import, the fetch and the JSON
 * parse have run; it asserts nothing.
 *
 * Awaiting the request itself is deterministic: renderHook runs the effect inside
 * act, and the effect calls getApplicationStatus(), which assigns pendingRequest
 * synchronously before its first await. This call therefore hits the dedupe branch
 * and joins the SAME promise, with the hook's continuation already registered — so
 * the hook's setResult runs first and act flushes it on exit. Pair every use with
 * an exact fetch call-count assertion, which catches both a race past the dedupe
 * window (2 calls) and an unstubbed transport (0 calls).
 */
export const settleAppStatus = async (
  mod: typeof import("hooks/app-status"),
): Promise<void> => {
  await act(async () => {
    await mod.getApplicationStatus();
    // The hooks' own fetchData() continuation hop.
    await Promise.resolve();
  });
};
