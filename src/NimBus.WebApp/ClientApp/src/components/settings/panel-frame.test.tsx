import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { useState } from "react";
import {
  PanelFooter,
  PanelSection,
  SettingsPanelFrame,
  useInPanel,
  usePanelDirty,
  usePanelReviewing,
} from "./panel-frame";

afterEach(() => cleanup());

const TABS = [
  { id: "one", label: "First" },
  { id: "two", label: "Second" },
];

// A stand-in settings component using every frame helper.
function Feature() {
  const [count, setCount] = useState(0);
  const [reviewing, setReviewing] = useState(false);
  usePanelDirty(count);
  usePanelReviewing(reviewing);
  const inPanel = useInPanel();
  return (
    <div data-testid="feature" data-in-panel={String(inPanel)}>
      <PanelSection tab="one">
        <p>Section one</p>
      </PanelSection>
      <PanelSection tab="two">
        <p>Section two</p>
      </PanelSection>
      {reviewing && <p>Review screen</p>}
      <PanelFooter>
        <button type="button" onClick={() => setCount((c) => c + 1)}>
          Edit
        </button>
        <button type="button" onClick={() => setReviewing((r) => !r)}>
          Toggle review
        </button>
      </PanelFooter>
    </div>
  );
}

function renderFrame(activeTab = "one", onDirtyChange = vi.fn(), onTabChange = vi.fn()) {
  render(
    <SettingsPanelFrame
      title="Feature"
      tabs={TABS}
      activeTab={activeTab}
      onTabChange={onTabChange}
      onDirtyChange={onDirtyChange}
      applies="Applies immediately"
    >
      <Feature />
    </SettingsPanelFrame>,
  );
  return { onDirtyChange, onTabChange };
}

describe("Standalone (no frame)", () => {
  it("renders every section and the footer inline, as before", () => {
    render(<Feature />);

    expect(screen.getByText("Section one")).toBeTruthy();
    expect(screen.getByText("Section two")).toBeTruthy();
    expect(screen.getByTestId("feature").contains(screen.getByText("Edit"))).toBe(true);
    expect(screen.getByTestId("feature").dataset.inPanel).toBe("false");
  });
});

describe("SettingsPanelFrame", () => {
  it("shows only the active tab's sections", () => {
    renderFrame("two");

    expect(screen.queryByText("Section one")).toBeNull();
    expect(screen.getByText("Section two")).toBeTruthy();
    expect(
      screen.getByRole("tab", { name: "Second" }).getAttribute("aria-selected"),
    ).toBe("true");
    expect(screen.getByTestId("feature").dataset.inPanel).toBe("true");
  });

  it("reports tab clicks", () => {
    const { onTabChange } = renderFrame("one");

    fireEvent.click(screen.getByRole("tab", { name: "Second" }));

    expect(onTabChange).toHaveBeenCalledWith("two");
  });

  it("moves the component's footer into its sticky footer", () => {
    renderFrame();

    const footer = screen.getByRole("contentinfo");
    expect(within(footer).getByRole("button", { name: "Edit" })).toBeTruthy();
    expect(screen.getByTestId("feature").contains(screen.getByText("Edit"))).toBe(false);
  });

  it("states when changes apply and reports the unsaved count", () => {
    const { onDirtyChange } = renderFrame();
    const footer = screen.getByRole("contentinfo");
    expect(footer.textContent).toContain("No unsaved changes · Applies immediately");

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));

    expect(footer.textContent).toContain("2 unsaved changes");
    expect(onDirtyChange).toHaveBeenLastCalledWith(2);
  });

  it("replaces the tabs and sections with the review screen while reviewing", () => {
    renderFrame();

    fireEvent.click(screen.getByRole("button", { name: "Toggle review" }));

    expect(screen.queryByRole("tablist")).toBeNull();
    expect(screen.queryByText("Section one")).toBeNull();
    expect(screen.getByText("Review screen")).toBeTruthy();
  });

  it("has no tab bar for a single-tab feature", () => {
    render(
      <SettingsPanelFrame
        title="Feature"
        tabs={[{ id: "only", label: "Only" }]}
        activeTab="only"
        onTabChange={vi.fn()}
        onDirtyChange={vi.fn()}
        applies="Applies immediately"
      >
        <p>Body</p>
      </SettingsPanelFrame>,
    );

    expect(screen.queryByRole("tablist")).toBeNull();
    expect(screen.getByText("Body")).toBeTruthy();
  });
});
