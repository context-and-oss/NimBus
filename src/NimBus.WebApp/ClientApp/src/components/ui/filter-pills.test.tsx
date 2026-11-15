import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import * as api from "api-client";
import {
  IdSearchBox,
  PillPopover,
  SingleSelectContent,
  TimePill,
} from "./filter-pills";

afterEach(cleanup);

const EMPTY = { period: "", from: "", to: "" };

describe("TimePill", () => {
  it("shows any time and applies a preset, clearing a custom range", () => {
    const onApply = vi.fn();
    render(
      <TimePill
        label="Enqueued"
        value={{ period: "", from: "2026-11-10T08:00", to: "" }}
        onApply={onApply}
      />,
    );

    fireEvent.click(
      screen.getByRole("button", { name: "Enqueued time range" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Last 7 days" }));

    expect(onApply).toHaveBeenCalledWith({
      period: api.Period._7d,
      from: "",
      to: "",
    });
  });

  it("returns to any time", () => {
    const onApply = vi.fn();
    render(
      <TimePill
        label="Updated"
        value={{ period: api.Period._1d, from: "", to: "" }}
        onApply={onApply}
      />,
    );
    expect(
      screen.getByRole("button", { name: "Updated time range" }).textContent,
    ).toContain("Last 24 hours");

    fireEvent.click(screen.getByRole("button", { name: "Updated time range" }));
    fireEvent.click(screen.getByRole("button", { name: "Any time" }));

    expect(onApply).toHaveBeenCalledWith(EMPTY);
  });

  it("applies a custom range as local input values, longer than 90 days", () => {
    const onApply = vi.fn();
    render(<TimePill label="Enqueued" value={EMPTY} onApply={onApply} />);

    fireEvent.click(
      screen.getByRole("button", { name: "Enqueued time range" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Custom…" }));
    fireEvent.change(screen.getByLabelText("Start time (local)"), {
      target: { value: "2026-01-01T08:00" },
    });
    fireEvent.change(screen.getByLabelText("End time (local)"), {
      target: { value: "2026-09-21T17:30" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      period: "",
      from: "2026-01-01T08:00",
      to: "2026-09-21T17:30",
    });
  });
});

describe("SingleSelectContent", () => {
  it("applies the picked option and closes", () => {
    const onApply = vi.fn();
    render(
      <PillPopover
        label="Message type"
        content={(close) => (
          <SingleSelectContent
            options={[
              { value: "", label: "Any type" },
              { value: "errorResponse", label: "ErrorResponse" },
            ]}
            value=""
            onApply={onApply}
            close={close}
          />
        )}
      >
        Message type
      </PillPopover>,
    );

    fireEvent.click(screen.getByRole("button", { name: "Message type" }));
    expect(
      screen
        .getByRole("button", { name: "Any type" })
        .getAttribute("aria-pressed"),
    ).toBe("true");
    fireEvent.click(screen.getByRole("button", { name: "ErrorResponse" }));

    expect(onApply).toHaveBeenCalledWith("errorResponse");
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

describe("IdSearchBox", () => {
  const prefixes = { event: "eventId", session: "sessionId" } as const;

  it("shows the applied IDs and submits the parsed query on Enter", () => {
    const onSubmit = vi.fn();
    render(
      <IdSearchBox
        label="Search events"
        placeholder="Search"
        prefixes={prefixes}
        bare="eventId"
        value={{ eventId: "e1", sessionId: "" }}
        onSubmit={onSubmit}
      />,
    );
    const box = screen.getByRole("searchbox", { name: "Search events" });
    expect((box as HTMLInputElement).value).toBe("event:e1");

    fireEvent.change(box, { target: { value: "session:s9" } });
    fireEvent.keyDown(box, { key: "Enter" });

    expect(onSubmit).toHaveBeenCalledWith({ eventId: "", sessionId: "s9" });
  });

  it("clears the applied IDs", () => {
    const onSubmit = vi.fn();
    render(
      <IdSearchBox
        label="Search events"
        placeholder="Search"
        prefixes={prefixes}
        bare="eventId"
        value={{ eventId: "", sessionId: "s1" }}
        onSubmit={onSubmit}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "Clear search" }));
    expect(onSubmit).toHaveBeenCalledWith({ eventId: "", sessionId: "" });
  });
});
