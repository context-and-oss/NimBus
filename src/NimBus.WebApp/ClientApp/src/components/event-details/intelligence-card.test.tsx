import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import IntelligenceCard from "./intelligence-card";

const originalFetch = globalThis.fetch;
const recordable = () => new Response(JSON.stringify({ status: "Recordable", canRecord: true, contractVersion: 1 }));
const readOnly = () => new Response(JSON.stringify({ status: "ReadOnly", canRecord: false, contractVersion: 1 }));

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  globalThis.fetch = originalFetch;
});

describe("IntelligenceCard", () => {
  it("shows a recorded result without offering analysis, and never posts (v5.0.0)", async () => {
    // The built-in provider is removed: even a status that still claims canAnalyze gets no Analyze action.
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Recordable", canAnalyze: true, canRecord: true, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1,
        externalDependencyLikelihood: 0, guidance: "Investigate", revision: 1, provider: "Agent", model: "agent-model",
        createdAtUtc: "2026-11-16T00:00:00Z",
      })));
    globalThis.fetch = fetchMock as typeof fetch;

    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);

    expect(await screen.findByText("business_rule (90%)")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /analyze/i })).toBeNull();
    expect(fetchMock.mock.calls.some(call => (call[1] as RequestInit | undefined)?.method === "POST")).toBe(false);
  });

  it("hides a 4.x Ready status: the provider is gone", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 1 })));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    expect(screen.queryByText(/Failure intelligence/)).toBeNull();
  });

  it("polls while a classification is being stored and shows it once saved", async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(recordable())
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "AnalysisInProgress" }), { status: 409 }))
      .mockResolvedValueOnce(recordable())
      .mockResolvedValueOnce(new Response(JSON.stringify({
        category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1,
        externalDependencyLikelihood: 0, guidance: "Investigate", revision: 2, provider: "Agent", model: "test",
        createdAtUtc: "2026-09-20T00:00:00Z",
      })));
    globalThis.fetch = fetchMock as typeof fetch;

    await act(async () => {
      render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    });
    expect(screen.getByText("A classification is being stored.")).toBeTruthy();

    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });

    expect(screen.getByText("business_rule (90%)")).toBeTruthy();
    expect(screen.queryByText("A classification is being stored.")).toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(10000); });
    expect(fetchMock).toHaveBeenCalledTimes(4);
  });

  it("reports an unknown outcome of the latest classification", async () => {
    globalThis.fetch = vi.fn()
      .mockResolvedValueOnce(recordable())
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "AnalysisOutcomeUnknown" }), { status: 409 })) as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    expect(await screen.findByText("The outcome of the latest classification is unknown.")).toBeTruthy();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("hides unsupported contract versions without loading", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ status: "Recordable", canRecord: true, contractVersion: 99 })));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    expect(screen.queryByText("Failure intelligence")).toBeNull();
  });

  it("clears a previous occurrence when navigating", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(recordable())
      .mockResolvedValueOnce(new Response(JSON.stringify({ category: "old_failure", categoryConfidence: 1 })))
      .mockResolvedValueOnce(recordable())
      .mockResolvedValueOnce(new Response(null, { status: 404 }));
    globalThis.fetch = fetchMock as typeof fetch;
    const view = render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await screen.findByText("old_failure (100%)");
    view.rerender(<IntelligenceCard endpointId="orders" eventId="event-2" messageId="message-2" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(4));
    expect(screen.queryByText("old_failure (100%)")).toBeNull();
  });

  it("shows a saved result in read-only mode and says agents can't record", async () => {
    // Spec 043 read-only mode: recording is unavailable, but saved results stay readable.
    globalThis.fetch = vi.fn()
      .mockResolvedValueOnce(readOnly())
      .mockResolvedValueOnce(new Response(JSON.stringify({ category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1, externalDependencyLikelihood: 0, guidance: "ChangeLikelyRequired", revision: 2, model: "test", createdAtUtc: "2026-09-20T00:00:00Z" }))) as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await screen.findByText("business_rule (90%)");
    expect(screen.queryByRole("button")).toBeNull();
    expect(screen.getByText(/Agents can't record new classifications in this deployment/)).toBeTruthy();
  });

  it("shows an agent's recording with its provenance, fault origin and rationale as plain text", async () => {
    // Spec 044 §6.8: an agent result names who recorded it; the rationale is untrusted text.
    globalThis.fetch = vi.fn()
      .mockResolvedValueOnce(recordable())
      .mockResolvedValueOnce(new Response(JSON.stringify({
        category: "missing_reference_data", categoryConfidence: 0.85, retryLikelihood: 0.1, changeRequiredLikelihood: 0.9,
        externalDependencyLikelihood: 0.2, guidance: "ChangeLikelyRequired", revision: 1, provider: "Agent", model: "claude-opus-5-5",
        createdAtUtc: "2026-11-11T09:00:00Z", requestedBy: "ops@example.com", questionSetVersion: 2,
        faultOrigin: "source_data", faultOriginConfidence: 0.8, clientId: "44444444-4444-4444-4444-444444444444",
        rationale: "Field customerId is empty. <script>alert(1)</script>",
      }))) as typeof fetch;

    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);

    await screen.findByText("missing_reference_data (85%)");
    expect(screen.getByText(/Recorded by an agent/)).toBeTruthy();
    expect(screen.getByText(/Model claude-opus-5-5 \(as declared\)/)).toBeTruthy();
    expect(screen.getByText(/Client 44444444-4444-4444-4444-444444444444/)).toBeTruthy();
    expect(screen.getByText("Source data (80%)")).toBeTruthy();
    expect(screen.getByText("Field customerId is empty. <script>alert(1)</script>")).toBeTruthy();
    expect(document.querySelector("script")).toBeNull();
    expect(screen.getByText("Agents record classifications over MCP.")).toBeTruthy();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("stays hidden in recordable mode until an agent has recorded a classification", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(recordable())
      .mockResolvedValueOnce(new Response(null, { status: 404 }));
    globalThis.fetch = fetchMock as typeof fetch;

    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    await act(async () => { await Promise.resolve(); });
    expect(screen.queryByText(/Failure intelligence/)).toBeNull();
  });

  it("marks a version 1 result's fault origin as not assessed", async () => {
    globalThis.fetch = vi.fn()
      .mockResolvedValueOnce(recordable())
      .mockResolvedValueOnce(new Response(JSON.stringify({ category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1, externalDependencyLikelihood: 0, revision: 2, provider: "TypeSafe", model: "jev-1.13.0", createdAtUtc: "2026-09-20T00:00:00Z", questionSetVersion: 1 }))) as typeof fetch;

    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);

    await screen.findByText("business_rule (90%)");
    expect(screen.getByText("Not assessed")).toBeTruthy();
    expect(screen.queryByText(/Recorded by an agent/)).toBeNull();
  });

  it("stays hidden in read-only mode when nothing was saved", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(readOnly())
      .mockResolvedValueOnce(new Response(null, { status: 404 }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(screen.queryByText(/Failure intelligence/)).toBeNull();
  });

  it("stays hidden in read-only mode when the saved result is not readable yet", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(readOnly())
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "AnalysisInProgress" }), { status: 409 }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(screen.queryByText(/Failure intelligence/)).toBeNull();
  });

  it("shows expandable classification details", async () => {
    globalThis.fetch = vi.fn()
      .mockResolvedValueOnce(recordable())
      .mockResolvedValueOnce(new Response(JSON.stringify({
        category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1,
        externalDependencyLikelihood: 0, guidance: "Investigate", revision: 1, model: "test",
        createdAtUtc: "2026-09-20T00:00:00Z", categoryProbabilities: { business_rule: 0.7, unknown: 0.2, transient_dependency: 0.1 }, questionSetVersion: 1,
      }))) as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await screen.findByText("business_rule (90%)");
    await userEvent.click(screen.getByText("Classification details"));
    expect(screen.getByText("unknown: 20%")).toBeTruthy();
    expect(screen.getByText("transient_dependency: 10%")).toBeTruthy();
    expect(screen.getByText("Question set v1")).toBeTruthy();
  });

  it("names the provider of a pre-5.0 result, says threshold settings were not recorded, and marks the linked occurrence", async () => {
    const scrollIntoView = vi.fn();
    const original = Element.prototype.scrollIntoView;
    Element.prototype.scrollIntoView = scrollIntoView;
    try {
      globalThis.fetch = vi.fn()
        .mockResolvedValueOnce(recordable())
        .mockResolvedValueOnce(new Response(JSON.stringify({
          category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1,
          externalDependencyLikelihood: 0, guidance: "Investigate", revision: 2, provider: "TypeSafe", model: "test",
          createdAtUtc: "2026-09-20T00:00:00Z", questionSetVersion: 1,
        }))) as typeof fetch;
      const view = render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" highlighted />);

      await screen.findByText("business_rule (90%)");
      expect(screen.getByText(/Revision 2 · TypeSafe · test/)).toBeTruthy();
      expect(screen.getByText(/built-in provider \(before NimBus 5\.0\)/)).toBeTruthy();
      await userEvent.click(screen.getByText("Classification details"));
      expect(screen.getByText("Threshold settings not recorded")).toBeTruthy();
      expect(view.container.querySelector('[aria-current="true"]')).toBeTruthy();
      expect(scrollIntoView).toHaveBeenCalledTimes(1);
    } finally {
      Element.prototype.scrollIntoView = original;
    }
  });

  it("stays hidden when the feature status route is absent", async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 404 })) as unknown as typeof fetch;

    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);

    await waitFor(() => expect(screen.queryByText("Failure intelligence")).toBeNull());
  });

  it("reads the exact failure message occurrence", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(recordable())
      .mockResolvedValueOnce(new Response(JSON.stringify({
        category: "transient_dependency", categoryConfidence: 0.91, retryLikelihood: 0.82, changeRequiredLikelihood: 0.12,
        externalDependencyLikelihood: 0.97, guidance: "RetryMayHelp", revision: 1, provider: "Agent", model: "agent-model",
        createdAtUtc: "2026-09-20T00:00:00Z",
      })));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="failure-message-7" resolutionStatus="DeadLettered" />);

    await screen.findByText("transient_dependency (91%)");
    expect(screen.getByText(/A manual resubmit is reasonable once the dependency is healthy/)).toBeTruthy();
    expect(screen.getByText(/A temporary downstream or infrastructure condition/)).toBeTruthy();
    expect(fetchMock.mock.calls[1][0]).toContain("/failures/event-1/failure-message-7/classification");
  });
});
