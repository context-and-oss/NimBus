import { afterEach, describe, expect, it, vi } from "vitest";
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
} from "./workspace";
import { RowSelect, selectableRowClass } from "./selectable-row";

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

  it("has no inspector column unless one is given", () => {
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
