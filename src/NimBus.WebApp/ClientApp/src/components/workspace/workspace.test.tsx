import { afterEach, describe, expect, it, vi } from "vitest";
import { useState } from "react";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import {
  Inspector,
  InspectorList,
  InspectorTiles,
  Workspace,
  WorkspaceChip,
  WorkspaceSection,
  WorkspaceStat,
  WorkspaceTabs,
  InspectorPortal,
} from "./workspace";
import { RowActionsCell, RowSelect, selectableRowClass } from "./selectable-row";

afterEach(cleanup);

describe("Workspace", () => {
  it("lays out the chips, header, table section, inspector and footer", () => {
    render(
      <Workspace
        title="Topology"
        subtitle="The live namespace."
        chipsLabel="Topic"
        chips={<WorkspaceChip selected onSelect={() => undefined}>All topics</WorkspaceChip>}
        stats={<WorkspaceStat label="Topics" value="9" />}
        actions={<button type="button">Export</button>}
        inspector={<Inspector title="orders" eyebrow="Subscription" />}
        footer={<span>No unsaved changes</span>}
      >
        <WorkspaceSection title="Subscriptions" count={5} intro="Live counts.">
          <table aria-label="Subscriptions" />
        </WorkspaceSection>
      </Workspace>,
    );

    expect(screen.getByRole("heading", { level: 1, name: "Topology" })).toBeTruthy();
    expect(screen.getByText("Topic")).toBeTruthy();
    expect(screen.getByRole("button", { name: "All topics" }).getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByText("Topics")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Export" })).toBeTruthy();
    expect(screen.getByRole("region", { name: "Subscriptions" })).toBeTruthy();
    const inspector = screen.getByRole("complementary", { name: "Inspector" });
    expect(within(inspector).getByRole("heading", { level: 3, name: "orders" })).toBeTruthy();
    expect(screen.getByText("No unsaved changes")).toBeTruthy();
  });

  it("has no inspector drawer unless one is given", () => {
    render(
      <Workspace title="Settings">
        <p>form</p>
      </Workspace>,
    );
    expect(screen.queryByRole("complementary")).toBeNull();
  });
});

describe("WorkspaceStat", () => {
  it("is a shortcut only when it has somewhere to go", () => {
    const onClick = vi.fn();
    render(
      <>
        <WorkspaceStat label="Failed" value="12" tone="danger" onClick={onClick} />
        <WorkspaceStat label="Topics" value="9" />
      </>,
    );
    fireEvent.click(screen.getByRole("button", { name: /Failed/ }));
    expect(onClick).toHaveBeenCalledOnce();
    expect(screen.queryByRole("button", { name: /Topics/ })).toBeNull();
  });
});

describe("WorkspaceTabs", () => {
  const tabs = [
    { id: "subs", label: "Subscriptions", count: 5 },
    { id: "drift", label: "Catalog drift" },
    { id: "storage", label: "Storage", disabled: true, title: "Cosmos DB only" },
  ];

  it("marks the open tab and opens another on click", () => {
    const onChange = vi.fn();
    render(<WorkspaceTabs label="Topology views" tabs={tabs} active="subs" onChange={onChange} />);

    const list = screen.getByRole("tablist", { name: "Topology views" });
    expect(within(list).getByRole("tab", { name: /Subscriptions/ }).getAttribute("aria-selected")).toBe("true");
    fireEvent.click(within(list).getByRole("tab", { name: "Catalog drift" }));
    expect(onChange).toHaveBeenCalledWith("drift");
  });

  it("does not open a disabled tab, by click or by arrow key", () => {
    const onChange = vi.fn();
    render(<WorkspaceTabs label="Topology views" tabs={tabs} active="drift" onChange={onChange} />);

    fireEvent.click(screen.getByRole("tab", { name: "Storage" }));
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.keyDown(screen.getByRole("tablist"), { key: "ArrowRight" });
    expect(onChange).toHaveBeenCalledWith("subs");
  });
});

describe("Inspector", () => {
  it("asks for a row when nothing is selected", () => {
    render(<Inspector />);
    expect(screen.getByText("Select a row to see its details here.")).toBeTruthy();
  });

  it("shows the selected row's figures and what can be opened from it", () => {
    const onAction = vi.fn();
    render(
      <Inspector eyebrow="Endpoint" title="ErpEndpoint" state={<span>Paused</span>} description="Kept in Service Bus.">
        <InspectorTiles tiles={[{ label: "Failed", value: "12", tone: "danger" }]} />
        <InspectorList
          label="Operations on this endpoint"
          items={[{ key: "dlq", label: "Delete dead-lettered", meta: "Cleanup", actionLabel: "Open Delete dead-lettered", onAction }]}
        />
      </Inspector>,
    );

    expect(screen.getByText("Endpoint")).toBeTruthy();
    expect(screen.getByText("Paused")).toBeTruthy();
    expect(screen.getByText("12")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Open Delete dead-lettered" }));
    expect(onAction).toHaveBeenCalledOnce();
  });
});

describe("RowSelect", () => {
  it("selects its row and says whether it is selected", () => {
    const onSelect = vi.fn();
    render(
      <table>
        <tbody>
          <tr className={selectableRowClass(true)}>
            <td>
              <RowSelect selected onSelect={onSelect}>
                orders
              </RowSelect>
            </td>
          </tr>
        </tbody>
      </table>,
    );
    const button = screen.getByRole("button", { name: "orders" });
    expect(button.getAttribute("aria-pressed")).toBe("true");
    fireEvent.click(button);
    expect(onSelect).toHaveBeenCalledOnce();
  });
});

// A workspace whose table owns the selection, as the Manage pages' tables do.
function Topics({ onAction = () => undefined }: { onAction?: (name: string) => void }) {
  const [selected, setSelected] = useState<string>();
  const names = ["billing", "orders", "shipping"];
  return (
    <Workspace title="Topology" inspectorSlot>
      <table aria-label="Topics">
        <tbody>
          {names.map((name) => (
            <tr key={name} className={selectableRowClass(name === selected)}>
              <td>
                <RowSelect selected={name === selected} onSelect={() => setSelected(name)}>
                  {name}
                </RowSelect>
              </td>
              <td>{`${name} count`}</td>
              <RowActionsCell label={`Actions for ${name}`} actions={[{ label: "Purge…", destructive: true, onSelect: () => onAction(name) }]} />
            </tr>
          ))}
        </tbody>
      </table>
      <InspectorPortal>
        {selected ? <Inspector eyebrow="Topic" title={selected} onClose={() => setSelected(undefined)} /> : <Inspector />}
      </InspectorPortal>
    </Workspace>
  );
}

const drawerOpen = () => screen.getByTestId("inspector-drawer").getAttribute("data-open") === "true";
const inspectorHeading = () => within(screen.getByRole("complementary", { name: "Inspector" })).getByRole("heading", { level: 3 });

describe("Workspace inspector drawer", () => {
  it("stays closed until a row is selected, and a click anywhere on the row selects it", () => {
    render(<Topics />);
    expect(screen.queryByRole("complementary", { name: "Inspector" })).toBeNull();

    fireEvent.click(screen.getByText("orders count"));

    expect(inspectorHeading().textContent).toBe("orders");
    expect(screen.getByRole("button", { name: "orders" }).getAttribute("aria-pressed")).toBe("true");
  });

  it("opens once the selection has rendered into it", async () => {
    render(<Topics />);
    expect(drawerOpen()).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "orders" }));
    await vi.waitFor(() => expect(drawerOpen()).toBe(true));
  });

  it("closes from its close button and from Escape, and hands focus back to the row", () => {
    render(<Topics />);
    fireEvent.click(screen.getByRole("button", { name: "orders" }));
    fireEvent.click(screen.getByRole("button", { name: "Close inspector" }));
    expect(screen.queryByRole("complementary", { name: "Inspector" })).toBeNull();
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "orders" }));

    fireEvent.click(screen.getByRole("button", { name: "billing" }));
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("complementary", { name: "Inspector" })).toBeNull();
  });

  it("steps through the rows with its buttons and the arrow keys, stopping at either end", async () => {
    render(<Topics />);
    fireEvent.click(screen.getByRole("button", { name: "orders" }));

    fireEvent.click(screen.getByRole("button", { name: "Next row" }));
    expect(inspectorHeading().textContent).toBe("shipping");
    await vi.waitFor(() =>
      expect((screen.getByRole("button", { name: "Next row" }) as HTMLButtonElement).disabled).toBe(true),
    );

    fireEvent.keyDown(document, { key: "ArrowUp" });
    fireEvent.keyDown(document, { key: "ArrowUp" });
    expect(inspectorHeading().textContent).toBe("billing");
    await vi.waitFor(() =>
      expect((screen.getByRole("button", { name: "Previous row" }) as HTMLButtonElement).disabled).toBe(true),
    );
  });

  it("leaves the arrow keys to a field that has focus", () => {
    render(
      <>
        <Topics />
        <input aria-label="Filter" />
      </>,
    );
    fireEvent.click(screen.getByRole("button", { name: "orders" }));
    fireEvent.keyDown(screen.getByLabelText("Filter"), { key: "ArrowDown" });
    expect(inspectorHeading().textContent).toBe("orders");
  });

  it("runs a row's menu action without selecting the row, and Escape closes only the menu", () => {
    const onAction = vi.fn();
    render(<Topics onAction={onAction} />);
    fireEvent.click(screen.getByRole("button", { name: "orders" }));

    fireEvent.click(screen.getByRole("button", { name: "Actions for billing" }));
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("menu")).toBeNull();
    expect(inspectorHeading().textContent).toBe("orders");

    fireEvent.click(screen.getByRole("button", { name: "Actions for billing" }));
    fireEvent.click(within(screen.getByRole("menu")).getByRole("menuitem", { name: "Purge…" }));
    expect(onAction).toHaveBeenCalledWith("billing");
    expect(inspectorHeading().textContent).toBe("orders");
  });
});
