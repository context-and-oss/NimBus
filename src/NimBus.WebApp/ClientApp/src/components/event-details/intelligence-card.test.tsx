import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import IntelligenceCard from "./intelligence-card";

const originalFetch = globalThis.fetch;

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  globalThis.fetch = originalFetch;
});

describe("IntelligenceCard", () => {
  it("clears active and historical labels when polling returns a completed result", async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "AnalysisInProgress" }), { status: 409 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1,
        externalDependencyLikelihood: 0, guidance: "Investigate", revision: 2, model: "test",
        createdAtUtc: "2026-09-20T00:00:00Z",
      })));
    globalThis.fetch = fetchMock as typeof fetch;

    await act(async () => {
      render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    });
    expect(screen.getByText("Analyzing failure…")).toBeTruthy();

    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });

    expect(screen.getByText("business_rule (90%)")).toBeTruthy();
    expect(screen.queryByText("Analyzing failure…")).toBeNull();
    expect(screen.queryByText(/Historical result/)).toBeNull();
    expect(screen.queryByText("Analysis is in progress.")).toBeNull();
    expect((screen.getByRole("button", { name: "Re-analyze" }) as HTMLButtonElement).disabled).toBe(false);
    await act(async () => { await vi.advanceTimersByTimeAsync(10000); });
    expect(fetchMock).toHaveBeenCalledTimes(4);
  });

  it("hides unsupported contract versions without loading or posting", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 99 })));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    expect(screen.queryByText("Failure intelligence")).toBeNull();
  });

  it("clears a previous occurrence when navigating", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: false, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ category: "old_failure", categoryConfidence: 1 })))
      .mockResolvedValueOnce(new Response(null, { status: 404 }));
    globalThis.fetch = fetchMock as typeof fetch;
    const view = render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await screen.findByText("old_failure (100%)");
    view.rerender(<IntelligenceCard endpointId="orders" eventId="event-2" messageId="message-2" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    expect(screen.queryByText("old_failure (100%)")).toBeNull();
  });
  it("shows a saved result in read-only mode without offering analysis", async () => {
    // Spec 043 read-only mode: no provider is configured, but saved results stay readable.
    globalThis.fetch = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "ProviderNotConfigured", canAnalyze: false, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1, externalDependencyLikelihood: 0, guidance: "ChangeLikelyRequired", revision: 2, model: "test", createdAtUtc: "2026-09-20T00:00:00Z" }))) as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await screen.findByText("business_rule (90%)");
    expect(screen.queryByRole("button")).toBeNull();
    expect(screen.getByText("New analysis is unavailable because no provider is configured.")).toBeTruthy();
  });

  it("stays hidden in read-only mode when nothing was saved", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "ProviderNotConfigured", canAnalyze: false, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(null, { status: 404 }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(screen.queryByText(/Failure intelligence/)).toBeNull();
    expect(screen.queryByText("No analysis has been performed.")).toBeNull();
  });

  it("stays hidden in read-only mode when the saved result is not readable yet", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "ProviderNotConfigured", canAnalyze: false, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "AnalysisInProgress" }), { status: 409 }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(screen.queryByText(/Failure intelligence/)).toBeNull();
  });

  it("shows existing results to Readers without offering paid analysis", async () => {
    globalThis.fetch = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: false, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1, externalDependencyLikelihood: 0, revision: 2, model: "test", createdAtUtc: "2026-09-20T00:00:00Z" }))) as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await screen.findByText("business_rule (90%)");
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("shows explicit empty state and expandable classification details", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(null, { status: 404 }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    expect(await screen.findByText("No analysis has been performed.")).toBeTruthy();

    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: false, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1,
        externalDependencyLikelihood: 0, guidance: "Investigate", revision: 1, model: "test",
        createdAtUtc: "2026-09-20T00:00:00Z", categoryProbabilities: { business_rule: 0.7, unknown: 0.2, transient_dependency: 0.1 }, questionSetVersion: 1,
      })));
    cleanup();
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await screen.findByText("business_rule (90%)");
    await userEvent.click(screen.getByText("Classification details"));
    expect(screen.getByText("unknown: 20%")).toBeTruthy();
    expect(screen.getByText("transient_dependency: 10%")).toBeTruthy();
    expect(screen.getByText("Question set v1")).toBeTruthy();
  });

  it("names the provider, says threshold settings were not recorded, and marks the linked occurrence", async () => {
    const scrollIntoView = vi.fn();
    const original = Element.prototype.scrollIntoView;
    Element.prototype.scrollIntoView = scrollIntoView;
    try {
      globalThis.fetch = vi.fn()
        .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: false, contractVersion: 1 })))
        .mockResolvedValueOnce(new Response(JSON.stringify({
          category: "business_rule", categoryConfidence: 0.9, retryLikelihood: 0, changeRequiredLikelihood: 1,
          externalDependencyLikelihood: 0, guidance: "Investigate", revision: 2, provider: "TypeSafe", model: "test",
          createdAtUtc: "2026-09-20T00:00:00Z", questionSetVersion: 1,
        }))) as typeof fetch;
      const view = render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" highlighted />);

      await screen.findByText("business_rule (90%)");
      expect(screen.getByText(/Revision 2 · TypeSafe · test/)).toBeTruthy();
      await userEvent.click(screen.getByText("Classification details"));
      expect(screen.getByText("Threshold settings not recorded")).toBeTruthy();
      expect(view.container.querySelector('[aria-current="true"]')).toBeTruthy();
      expect(scrollIntoView).toHaveBeenCalledTimes(1);
    } finally {
      Element.prototype.scrollIntoView = original;
    }
  });

  it("marks a saved result historical while a newer analysis is in progress", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ category: "unknown", categoryConfidence: 0.9, revision: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "AnalysisInProgress" }), { status: 409 }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await userEvent.click(await screen.findByRole("button", { name: "Re-analyze" }));
    expect(await screen.findByText(/Historical result/)).toBeTruthy();
    expect(screen.getByText("Analyzing failure…")).toBeTruthy();
  });

  it("forces a new request after an unknown outcome", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 1 })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "AnalysisOutcomeUnknown" }), { status: 409 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "ProviderUnavailable" }), { status: 503 }));
    globalThis.fetch = fetchMock as typeof fetch;
    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);
    await userEvent.click(await screen.findByRole("button", { name: "Re-analyze" }));
    expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual({ force: true });
  });
  it("stays hidden when the feature status route is absent", async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 404 })) as unknown as typeof fetch;

    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="message-1" resolutionStatus="Failed" />);

    await waitFor(() => expect(screen.queryByText("Failure intelligence")).toBeNull());
  });

  it("analyzes the exact failure message occurrence", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ status: "Ready", canAnalyze: true, contractVersion: 1 }), { status: 200 }))
      .mockResolvedValueOnce(new Response(null, { status: 404 }))
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            result: {
              category: "transient_dependency",
              categoryConfidence: 0.91,
              retryLikelihood: 0.82,
              changeRequiredLikelihood: 0.12,
              externalDependencyLikelihood: 0.97,
              guidance: "RetryMayHelp",
              revision: 1,
              model: "jev-1.13.0",
              createdAtUtc: "2026-09-20T00:00:00Z",
            },
            cached: false,
          }),
          { status: 200 },
        ),
      );
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    render(<IntelligenceCard endpointId="orders" eventId="event-1" messageId="failure-message-7" resolutionStatus="DeadLettered" />);

    await userEvent.click(await screen.findByRole("button", { name: "Analyze failure" }));
    await screen.findByText("transient_dependency (91%)");
    expect(screen.getByText(/A manual resubmit is reasonable once the dependency is healthy/)).toBeTruthy();
    expect(screen.getByText(/A temporary downstream or infrastructure condition/)).toBeTruthy();

    const postCall = fetchMock.mock.calls[2];
    expect(postCall[0]).toContain("/failures/event-1/failure-message-7/classification");
    expect((postCall[1] as RequestInit).headers).toMatchObject({ "Idempotency-Key": expect.any(String) });
  });
});
