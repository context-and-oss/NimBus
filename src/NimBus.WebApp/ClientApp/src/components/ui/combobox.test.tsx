import { afterEach, describe, expect, it, vi } from "vitest";
import { useState } from "react";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Combobox } from "./combobox";

const options = ["A", "B", "C"].map((v) => ({ value: v, label: v }));

function Harness({
  onCommit,
  initial = [],
}: {
  onCommit: (v: string[]) => void;
  initial?: string[];
}) {
  const [value, setValue] = useState<string[]>(initial);
  return (
    <div>
      <Combobox
        options={options}
        value={value}
        onChange={setValue}
        onCommit={onCommit}
        placeholder="Pick"
      />
      <p>outside</p>
    </div>
  );
}

afterEach(cleanup);

describe("Combobox onCommit", () => {
  it("commits once, when the list closes after several picks", async () => {
    const user = userEvent.setup();
    const onCommit = vi.fn();
    render(<Harness onCommit={onCommit} />);

    await user.click(screen.getByPlaceholderText("Pick"));
    await user.click(screen.getByText("A"));
    await user.click(screen.getByText("B"));
    expect(onCommit).not.toHaveBeenCalled();

    await user.click(screen.getByText("outside"));
    expect(onCommit).toHaveBeenCalledTimes(1);
    expect(onCommit).toHaveBeenCalledWith(["A", "B"]);
  });

  it("does not commit when the list closes unchanged", async () => {
    const user = userEvent.setup();
    const onCommit = vi.fn();
    render(<Harness onCommit={onCommit} />);

    await user.click(screen.getByPlaceholderText("Pick"));
    await user.click(screen.getByText("A"));
    // Once picked, "A" is also a chip; the list option comes last.
    await user.click(screen.getAllByText("A").at(-1)!);
    await user.click(screen.getByText("outside"));

    expect(onCommit).not.toHaveBeenCalled();
  });

  it("closes on Escape and commits", async () => {
    const user = userEvent.setup();
    const onCommit = vi.fn();
    render(<Harness onCommit={onCommit} />);

    await user.click(screen.getByPlaceholderText("Pick"));
    await user.click(screen.getByText("C"));
    await user.click(screen.getByRole("textbox"));
    await user.keyboard("{Escape}");

    expect(onCommit).toHaveBeenCalledWith(["C"]);
    expect(screen.queryByText("B")).toBeNull();
  });

  it("commits at once when a chip is removed with the list closed", async () => {
    const user = userEvent.setup();
    const onCommit = vi.fn();
    render(<Harness onCommit={onCommit} initial={["A", "B"]} />);

    await user.click(screen.getByRole("button", { name: "Remove A" }));

    expect(onCommit).toHaveBeenCalledWith(["B"]);
  });
});
