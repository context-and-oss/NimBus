import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import FailureIntelligenceSettings from "./failure-intelligence-settings";
import { renderInPanel } from "../../test-utils/render-in-panel";

const originalFetch = globalThis.fetch;
const settings = { enabled: true, includeEventPayload: false, includeRecentFailureHistory: true,
  maximumHistoryItems: 5, additionalRedactedKeys: [], allowedEndpoints: [],
  minimumCategoryConfidence: 0.6, retryLikely: 0.75, changeRequired: 0.75 };
const state = { active: settings, saved: settings, revision: "none", activeRevision: "none", restartRequired: false,
  startupLoadFailed: false, csrfToken: "csrf-test" };
afterEach(() => { cleanup(); globalThis.fetch = originalFetch; });

describe("Failure intelligence settings", () => {
  it("has no provider, model, API key or timeout fields (v5.0.0)", async () => {
    globalThis.fetch = vi.fn().mockResolvedValueOnce(Response.json(state)) as typeof fetch;
    render(<FailureIntelligenceSettings />);
    await screen.findByRole("switch", { name: "Agents may see redacted payloads" });
    expect(screen.queryByLabelText("Provider")).toBeNull();
    expect(screen.queryByLabelText("Model")).toBeNull();
    expect(screen.queryByLabelText(/API key/)).toBeNull();
    expect(screen.queryByLabelText(/timeout/i)).toBeNull();
    expect(screen.queryByText(/deprecated provider/i)).toBeNull();
  });

  it("reviews payload sharing and saves with CSRF and a revision without changing active settings", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(Response.json(state)).mockResolvedValueOnce(Response.json({
      ...state, saved: { ...settings, includeEventPayload: true }, revision: "new-revision", restartRequired: true,
    }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<FailureIntelligenceSettings />);
    await userEvent.click(await screen.findByRole("switch", { name: "Agents may see redacted payloads" }));
    expect(screen.getByText(/Event data will leave NimBus/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    expect((screen.getByRole("button", { name: "Save settings" }) as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(screen.getByRole("checkbox", { name: /I authorize/ }));
    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));
    await screen.findByText(/Settings saved. Restart all WebApp instances/);
    const request = fetchMock.mock.calls[1][1];
    expect(request.headers["X-NimBus-CSRF"]).toBe("csrf-test");
    const body = JSON.parse(request.body);
    expect(body).toMatchObject({ revision: "none", payloadSharingAcknowledged: true, settings: { includeEventPayload: true } });
    expect(body).not.toHaveProperty("apiKey");
    expect(body).not.toHaveProperty("clearApiKey");
    expect(body.settings).not.toHaveProperty("model");
    expect(body.settings).not.toHaveProperty("timeoutSeconds");
    expect(screen.getByText(/Active on this instance: enabled · payload excluded/)).toBeTruthy();
  });

  it("shows forbidden without rendering editable controls", async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 403 })) as typeof fetch;
    render(<FailureIntelligenceSettings />);
    await screen.findByText(/Only site Owners/);
    expect(screen.queryByRole("switch")).toBeNull();
  });

  it("preserves edits and explains concurrent-save conflicts", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(Response.json(state)).mockResolvedValueOnce(new Response(null, { status: 409 }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<FailureIntelligenceSettings />);
    const history = await screen.findByLabelText("Maximum history items");
    await userEvent.clear(history);
    await userEvent.type(history, "3");
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));
    await screen.findByText(/Another administrator/);
    expect((history as HTMLInputElement).value).toBe("3");
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
  });
  // Spec 038: the form now renders inside the Settings side panel, which owns
  // closing and full-screen phone layout, so it must not take over the viewport
  // itself or link back to the removed Admin page.
  it("leaves layout and navigation to the panel that hosts it", async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(Response.json(state)) as typeof fetch;
    const { container } = render(<FailureIntelligenceSettings />);
    await screen.findByRole("switch", { name: "Enable failure intelligence" });
    expect(screen.queryByRole("link", { name: /Back to Admin/ })).toBeNull();
    expect(container.querySelector("form")?.className).not.toMatch(/fixed/);
  });

  it("offers no review until something changed", async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(Response.json(state)) as typeof fetch;
    render(<FailureIntelligenceSettings />);
    await screen.findByRole("switch", { name: "Enable failure intelligence" });

    expect((screen.getByRole("button", { name: "Review changes" }) as HTMLButtonElement).disabled).toBe(true);
  });

  // Spec 038 slice 2: inside the Settings panel frame.
  describe("in the Settings panel", () => {
    it("shows one tab at a time", async () => {
      globalThis.fetch = vi.fn().mockResolvedValue(Response.json(state)) as typeof fetch;
      renderInPanel(<FailureIntelligenceSettings />, "failure-intelligence", "evidence");

      expect(await screen.findByRole("switch", { name: "Agents may see redacted payloads" })).toBeTruthy();
      expect(screen.queryByRole("switch", { name: "Enable failure intelligence" })).toBeNull();
      expect(screen.getByText("What leaves NimBus")).toBeTruthy();
    });

    it("counts unsaved edits and reviews them from the footer, replacing the tabs", async () => {
      globalThis.fetch = vi.fn().mockResolvedValue(Response.json(state)) as typeof fetch;
      const { onDirtyChange } = renderInPanel(<FailureIntelligenceSettings />, "failure-intelligence");

      await userEvent.click(await screen.findByRole("switch", { name: "Enable failure intelligence" }));
      expect(onDirtyChange).toHaveBeenLastCalledWith(1);

      // The footer's submit button sits outside the form; it still submits it.
      await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
      expect(screen.getByRole("region", { name: "Review settings" })).toBeTruthy();
      expect(screen.queryByRole("tablist")).toBeNull();
      expect(screen.queryByRole("switch", { name: "Enable failure intelligence" })).toBeNull();
    });
  });
});
