import { afterEach, describe, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import ResubmitEditor from "./resubmit-editor";

afterEach(cleanup);

describe("ResubmitEditor", () => {
  it("uses history that arrives after the editor opens without overwriting a draft", () => {
    const props = {
      eventTypeId: "Order",
      eventTypes: [],
      onClose: vi.fn(),
      onSubmit: vi.fn(),
    };
    const { rerender } = render(<ResubmitEditor {...props} payload="{}" />);
    rerender(<ResubmitEditor {...props} payload={'{"request":true}'} />);
    const input = screen.getByRole("textbox", {
      name: "Modified event:",
    }) as HTMLTextAreaElement;
    expect(input.value).toBe('{\n  "request": true\n}');
    fireEvent.change(input, { target: { value: '{\n  "draft": true\n}' } });
    rerender(<ResubmitEditor {...props} payload={'{"newer":true}'} />);
    expect(input.value).toBe('{\n  "draft": true\n}');
  });

  it("retains a draft after a rejected command and allows retry", async () => {
    const onSubmit = vi.fn().mockRejectedValue({ status: 503 });
    render(
      <ResubmitEditor
        eventTypeId="Order"
        eventTypes={[]}
        payload="{}"
        onClose={vi.fn()}
        onSubmit={onSubmit}
      />,
    );
    const input = screen.getByRole("textbox", {
      name: "Modified event:",
    }) as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: '{"draft":true}' } });
    fireEvent.click(screen.getByRole("button", { name: "Resubmit" }));
    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(input.value).toBe('{"draft":true}');
    await waitFor(() =>
      expect(
        (screen.getByRole("button", { name: "Resubmit" }) as HTMLButtonElement)
          .disabled,
      ).toBe(false),
    );
  });

  it("prevents another submission after a stale attempt is refused", async () => {
    render(
      <ResubmitEditor
        eventTypeId="Order"
        eventTypes={[]}
        payload="{}"
        onClose={vi.fn()}
        onSubmit={vi.fn().mockRejectedValue({ status: 409 })}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "Resubmit" }));
    await screen.findByRole("alert");
    expect(
      (screen.getByRole("button", { name: "Resubmit" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });
});
