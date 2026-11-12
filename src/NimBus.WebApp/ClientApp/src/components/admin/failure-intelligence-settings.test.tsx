import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import FailureIntelligenceSettings from "./failure-intelligence-settings";
import { renderInPanel } from "../../test-utils/render-in-panel";

const originalFetch = globalThis.fetch;
const settings = { enabled: true, model: "jev-1.13.0", includeEventPayload: false, includeRecentFailureHistory: true,
  maximumHistoryItems: 5, additionalRedactedKeys: [], allowedEndpoints: [], timeoutSeconds: 20,
  minimumCategoryConfidence: 0.6, retryLikely: 0.75, changeRequired: 0.75 };
const state = { active: settings, saved: settings, revision: "none", activeRevision: "none", restartRequired: false,
  startupLoadFailed: false, credentialConfigured: true, credentialSource: "deployment", savedApiKey: "none", csrfToken: "csrf-test" };
afterEach(() => { cleanup(); globalThis.fetch = originalFetch; });

describe("Failure intelligence settings", () => {
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
    expect(JSON.parse(request.body)).toMatchObject({ revision: "none", payloadSharingAcknowledged: true, apiKey: null, clearApiKey: false, settings: { includeEventPayload: true } });
    expect(screen.getByText(/Active on this instance: enabled · payload excluded · API key configured by deployment/)).toBeTruthy();
  });

  it("marks the provider fields deprecated with the removal version", async () => {
    // Spec 044 §6.8: agents record over MCP; the built-in provider goes in v5.0.0.
    globalThis.fetch = vi.fn().mockResolvedValueOnce(Response.json(state)) as typeof fetch;
    render(<FailureIntelligenceSettings />);
    const note = await screen.findByRole("note");
    expect(note.textContent).toMatch(/Deprecated/);
    expect(note.textContent).toMatch(/removed in NimBus 5\.0\.0/);
    expect((screen.getByLabelText("Provider") as HTMLInputElement).value).toBe("TypeSafe (deprecated)");
    expect(screen.getByLabelText(/Provider timeout \(seconds, deprecated\)/)).toBeTruthy();
  });

  it("sends a new API key as a masked write-only field and clears it after saving", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(Response.json({ ...state, credentialConfigured: false, credentialSource: "none" }))
      .mockResolvedValueOnce(Response.json({ ...state, credentialConfigured: false, credentialSource: "none", savedApiKey: "configured", revision: "rev-1", restartRequired: true }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<FailureIntelligenceSettings />);
    await screen.findByText(/Provider key is missing. Save one below/);
    const key = screen.getByLabelText(/API key/) as HTMLInputElement;
    expect(key.type).toBe("password");
    expect(key.autocomplete).toBe("new-password");
    await userEvent.type(key, " ts-live-key-123 ");
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    expect(screen.getByText(/API key replaced\./)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));
    await screen.findByText(/Settings saved/);
    expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toMatchObject({ apiKey: "ts-live-key-123", clearApiKey: false });
    expect((screen.getByLabelText(/New API key/) as HTMLInputElement).value).toBe("");
    expect(screen.getByText(/Saved key: configured/)).toBeTruthy();
    expect(screen.queryByText(/Provider key is missing/)).toBeNull();
    expect(document.body.textContent).not.toContain("ts-live-key-123");
  });

  it("removes a saved key without sending a value and warns when the saved key is unreadable", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(Response.json({ ...state, credentialSource: "saved", savedApiKey: "unreadable", revision: "rev-1" }))
      .mockResolvedValueOnce(Response.json({ ...state, credentialSource: "saved", savedApiKey: "none", revision: "rev-2", restartRequired: true }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<FailureIntelligenceSettings />);
    await screen.findByText(/cannot be used here/);
    await userEvent.click(screen.getByRole("checkbox", { name: /Remove the saved key/ }));
    expect((screen.getByLabelText(/New API key/) as HTMLInputElement).disabled).toBe(true);
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    expect(screen.getByText(/API key removed\./)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));
    await screen.findByText(/Settings saved/);
    expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toMatchObject({ revision: "rev-1", apiKey: null, clearApiKey: true });
    expect(screen.queryByRole("checkbox", { name: /Remove the saved key/ })).toBeNull();
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
    const model = await screen.findByLabelText("Model");
    await userEvent.clear(model);
    await userEvent.type(model, "jev-test");
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));
    await screen.findByText(/Another administrator/);
    expect((model as HTMLInputElement).value).toBe("jev-test");
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
  });
  // Spec 038: the form now renders inside the Settings side panel, which owns
  // closing and full-screen phone layout, so it must not take over the viewport
  // itself or link back to the removed Admin page.
  it("leaves layout and navigation to the panel that hosts it", async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(Response.json(state)) as typeof fetch;
    const { container } = render(<FailureIntelligenceSettings />);
    await screen.findByLabelText("Model");
    expect(screen.queryByRole("link", { name: /Back to Admin/ })).toBeNull();
    expect(container.querySelector("form")?.className).not.toMatch(/fixed/);
  });

  it("offers no review until something changed", async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(Response.json(state)) as typeof fetch;
    render(<FailureIntelligenceSettings />);
    await screen.findByLabelText("Model");

    expect((screen.getByRole("button", { name: "Review changes" }) as HTMLButtonElement).disabled).toBe(true);
  });

  // Spec 038 slice 2: inside the Settings panel frame.
  describe("in the Settings panel", () => {
    it("shows one tab at a time", async () => {
      globalThis.fetch = vi.fn().mockResolvedValue(Response.json(state)) as typeof fetch;
      renderInPanel(<FailureIntelligenceSettings />, "failure-intelligence", "evidence");

      expect(await screen.findByRole("switch", { name: "Agents may see redacted payloads" })).toBeTruthy();
      expect(screen.queryByLabelText("Model")).toBeNull();
      expect(screen.getByText("What leaves NimBus")).toBeTruthy();
    });

    it("counts unsaved edits and reviews them from the footer, replacing the tabs", async () => {
      globalThis.fetch = vi.fn().mockResolvedValue(Response.json(state)) as typeof fetch;
      const { onDirtyChange } = renderInPanel(<FailureIntelligenceSettings />, "failure-intelligence");
      const model = await screen.findByLabelText("Model");

      await userEvent.clear(model);
      await userEvent.type(model, "jev-2");
      expect(onDirtyChange).toHaveBeenLastCalledWith(1);

      // The footer's submit button sits outside the form; it still submits it.
      await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
      expect(screen.getByRole("region", { name: "Review settings" })).toBeTruthy();
      expect(screen.queryByRole("tablist")).toBeNull();
      expect(screen.queryByLabelText("Model")).toBeNull();
    });
  });
});
