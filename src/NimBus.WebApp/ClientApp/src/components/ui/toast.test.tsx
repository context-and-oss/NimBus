import { afterEach, describe, expect, it } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { Modal } from "./modal";
import { ToastProvider, emitToast } from "./toast";

afterEach(() => {
  cleanup();
});

describe("ToastProvider", () => {
  it("stacks toasts above an open Modal's overlay, so their actions stay clickable", () => {
    render(
      <ToastProvider>
        <Modal isOpen onClose={() => {}} label="Compose">
          dialog
        </Modal>
      </ToastProvider>,
    );

    act(() => emitToast({ title: "Publishing", duration: 0 }));

    const zIndex = (element: HTMLElement | null) => {
      const match = /\bz-(?:\[(\d+)\]|(\d+))/.exec(element?.className ?? "");
      return Number(match?.[1] ?? match?.[2] ?? 0);
    };
    const toastLayer = screen.getByRole("alert").parentElement;
    const overlay = screen.getByRole("dialog").parentElement;

    expect(zIndex(overlay)).toBe(50);
    expect(zIndex(toastLayer)).toBeGreaterThan(zIndex(overlay));
  });
});
