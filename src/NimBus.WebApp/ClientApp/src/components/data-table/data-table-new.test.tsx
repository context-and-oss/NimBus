import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import {
  render,
  screen,
  cleanup,
  fireEvent,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import DataTable from "./data-table-new";
import type { ITableHeadCell, ITableRow } from "./types";

// DataTable uses react-router for row-level navigation when `route` is set,
// so wrap everything in a MemoryRouter. No-op the toast provider since the
// table reads from it for action feedback.
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: () => {} }),
}));

// Spy on useNavigate so row-click navigation is observable; everything else
// (MemoryRouter included) stays real.
const navigateSpy = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => {
  const actual = await importOriginal<typeof import("react-router-dom")>();
  return { ...actual, useNavigate: () => navigateSpy };
});

const headCells: ITableHeadCell[] = [
  { id: "name", label: "Name", numeric: false },
  { id: "count", label: "Count", numeric: true },
];

const makeRow = (id: string, name: string, count: number): ITableRow => ({
  id,
  data: new Map([
    ["name", { value: name, searchValue: name }],
    ["count", { value: count, searchValue: count }],
  ]),
});

const renderTable = (ui: React.ReactElement) =>
  render(<MemoryRouter>{ui}</MemoryRouter>);

afterEach(() => cleanup());

describe("DataTable (smoke)", () => {
  it("renders header labels for all configured columns", () => {
    renderTable(<DataTable headCells={headCells} rows={[]} />);
    expect(screen.getByText("Name")).toBeDefined();
    expect(screen.getByText("Count")).toBeDefined();
  });

  it("renders row cell values from the per-row data map", () => {
    const rows = [makeRow("r1", "alpha", 42), makeRow("r2", "beta", 7)];
    renderTable(<DataTable headCells={headCells} rows={rows} />);

    expect(screen.getByText("alpha")).toBeDefined();
    expect(screen.getByText("beta")).toBeDefined();
    expect(screen.getByText("42")).toBeDefined();
    expect(screen.getByText("7")).toBeDefined();
  });

  it("renders without crashing when rows is empty", () => {
    renderTable(<DataTable headCells={headCells} rows={[]} />);
    // Header still renders; body is empty — the noDataMessage default is
    // "No data" but the exact wording isn't part of this smoke contract.
    expect(screen.getByText("Name")).toBeDefined();
  });
});

// ---------------------------------------------------------------------------
// Row navigation (ported from DIS 5adc92b7) — plain click navigates in place;
// Ctrl/Cmd-click and middle-click open the row's route in a new tab.
// ---------------------------------------------------------------------------
describe("DataTable row navigation", () => {
  const ROUTE = "/Message/Index/Bob/event-1/0";

  const routedRows: ITableRow[] = [
    {
      id: "row-1",
      route: ROUTE,
      data: new Map([
        ["name", { value: "AliceSaidHello", searchValue: "AliceSaidHello" }],
        ["count", { value: 1, searchValue: 1 }],
      ]),
    },
  ];

  let openedTab: { opener: unknown };

  beforeEach(() => {
    navigateSpy.mockReset();
    openedTab = { opener: {} };
    vi.stubGlobal(
      "open",
      vi.fn(() => openedTab),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function getRow(): HTMLElement {
    renderTable(
      <DataTable headCells={headCells} rows={routedRows} withToolbar={false} />,
    );
    return screen.getByText("AliceSaidHello").closest("tr") as HTMLElement;
  }

  function auxClick(row: HTMLElement, button: number) {
    fireEvent(
      row,
      new MouseEvent("auxclick", { bubbles: true, cancelable: true, button }),
    );
  }

  it("navigates in the same tab on a plain click", () => {
    const row = getRow();

    fireEvent.click(row);

    expect(navigateSpy).toHaveBeenCalledWith(ROUTE);
    expect(window.open).not.toHaveBeenCalled();
  });

  it("opens a new tab on Ctrl+click and nulls the opener", () => {
    const row = getRow();

    fireEvent.click(row, { ctrlKey: true });

    expect(window.open).toHaveBeenCalledWith(ROUTE, "_blank");
    expect(openedTab.opener).toBeNull();
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it("opens a new tab on Cmd+click (metaKey)", () => {
    const row = getRow();

    fireEvent.click(row, { metaKey: true });

    expect(window.open).toHaveBeenCalledWith(ROUTE, "_blank");
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it("opens a new tab on middle-click", () => {
    const row = getRow();

    auxClick(row, 1);

    expect(window.open).toHaveBeenCalledWith(ROUTE, "_blank");
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it("ignores a right-button auxclick (button 2)", () => {
    const row = getRow();

    auxClick(row, 2);

    expect(window.open).not.toHaveBeenCalled();
    expect(navigateSpy).not.toHaveBeenCalled();
  });
});

describe("DataTable row actions menu", () => {
  beforeEach(() => navigateSpy.mockReset());
  const actionRows = (onResubmit: () => boolean): ITableRow[] => [
    {
      ...makeRow("r1", "failed-row", 1),
      route: "/rows/r1",
      bodyActions: [
        {
          name: "Resubmit",
          description: "Send the message again",
          onClick: onResubmit,
        },
        { name: "Skip", onClick: () => false },
      ],
    },
    { ...makeRow("r2", "completed-row", 2), route: "/rows/r2" },
  ];

  it("shows a single ellipsis trigger only on rows that have actions", () => {
    renderTable(
      <DataTable
        headCells={headCells}
        rows={actionRows(() => false)}
        rowActions="menu"
      />,
    );

    expect(screen.getAllByRole("button", { name: "Actions" })).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "Resubmit" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Skip" })).toBeNull();
  });

  it("opens a dropdown listing the actions and runs the chosen one", () => {
    const onResubmit = vi.fn(() => false);
    renderTable(
      <DataTable
        headCells={headCells}
        rows={actionRows(onResubmit)}
        rowActions="menu"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    const menu = screen.getByRole("menu");
    expect(screen.queryByRole("dialog")).toBeNull();
    // Compact menu: the description is a tooltip, not inline text.
    expect(menu.textContent).not.toContain("Send the message again");
    expect(
      screen.getByRole("menuitem", { name: "Resubmit" }).getAttribute("title"),
    ).toBe("Send the message again");
    expect(screen.getByRole("menuitem", { name: "Skip" })).toBeDefined();

    fireEvent.click(screen.getByRole("menuitem", { name: /Resubmit/ }));

    expect(onResubmit).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("menu")).toBeNull();
    // Choosing an action must not also navigate the row.
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it("keeps inline buttons by default", () => {
    renderTable(
      <DataTable headCells={headCells} rows={actionRows(() => false)} />,
    );

    expect(screen.queryByRole("button", { name: "Actions" })).toBeNull();
    expect(screen.getByRole("button", { name: "Resubmit" })).toBeDefined();
  });
});

// Characterizes the @tanstack/react-table behaviors the table relies on, so a
// library upgrade that changes sorting, filtering, paging or selection fails here.
describe("DataTable (table behavior)", () => {
  const rows = (): ITableRow[] => [
    makeRow("a", "item 10", 3),
    makeRow("b", "item 2", 1),
    makeRow("c", "other", 2),
  ];

  const bodyRows = () => screen.getAllByRole("row").slice(1);
  const firstCellText = (cellIndex = 0) =>
    bodyRows().map((r) => within(r).getAllByRole("cell")[cellIndex].textContent);

  it("sorts text columns ascending first, then descending", () => {
    const words = [
      makeRow("a", "beta", 1),
      makeRow("b", "alpha", 2),
      makeRow("c", "gamma", 3),
    ];
    renderTable(<DataTable headCells={headCells} rows={words} />);

    fireEvent.click(screen.getByRole("columnheader", { name: /Name/ }));
    expect(firstCellText()).toEqual(["alpha", "beta", "gamma"]);

    fireEvent.click(screen.getByRole("columnheader", { name: /Name/ }));
    expect(firstCellText()).toEqual(["gamma", "beta", "alpha"]);
  });

  // react-table 8 chose the automatic sort function from the rows after the
  // tenth (a slice(10) bug), so tables of up to ten rows compared text with `<`
  // and put "item 10" before "item 2". From 9 it samples the first ten rows.
  it("sorts text with numbers in natural order, even in short tables", () => {
    renderTable(<DataTable headCells={headCells} rows={rows()} />);

    fireEvent.click(screen.getByRole("columnheader", { name: /Name/ }));
    expect(firstCellText()).toEqual(["item 2", "item 10", "other"]);
  });

  it("sorts number columns descending first", () => {
    renderTable(<DataTable headCells={headCells} rows={rows()} />);

    fireEvent.click(screen.getByRole("columnheader", { name: /Count/ }));
    expect(firstCellText()).toEqual(["item 10", "other", "item 2"]);
  });

  it("filters rows from the search box across cells", () => {
    renderTable(<DataTable headCells={headCells} rows={rows()} />);

    fireEvent.change(screen.getByPlaceholderText("Search..."), {
      target: { value: "OTH" },
    });
    expect(firstCellText()).toEqual(["other"]);
  });

  it("pages rows and reports the visible range", () => {
    renderTable(
      <DataTable headCells={headCells} rows={rows()} dataRowsPerPage={2} />,
    );

    expect(bodyRows()).toHaveLength(2);
    expect(screen.getByText(/Showing 1 to 2 of 3 results/)).toBeDefined();
    expect(screen.getByText(/Page 1 of 2/)).toBeDefined();

    fireEvent.click(screen.getByRole("button", { name: "Next" }));

    expect(bodyRows()).toHaveLength(1);
    expect(screen.getByText(/Showing 3 to 3 of 3 results/)).toBeDefined();
    expect(
      (screen.getByRole("button", { name: "Next" }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it("shows select-all as indeterminate only while some, not all, rows are selected", () => {
    renderTable(<DataTable headCells={headCells} rows={rows()} withCheckboxes />);
    const selectAll = () =>
      screen.getByRole("checkbox", { name: "Select all" }) as HTMLInputElement;
    const rowBoxes = () =>
      screen.getAllByRole("checkbox", { name: "Select row" }) as HTMLInputElement[];

    fireEvent.click(rowBoxes()[0]);
    expect(selectAll().checked).toBe(false);
    expect(selectAll().indeterminate).toBe(true);

    fireEvent.click(selectAll());
    expect(rowBoxes().every((b) => b.checked)).toBe(true);
    expect(selectAll().checked).toBe(true);
    expect(selectAll().indeterminate).toBe(false);
  });

  it("selects only the clicked row on Shift+click, not a range", () => {
    renderTable(<DataTable headCells={headCells} rows={rows()} withCheckboxes />);
    const rowBoxes = () =>
      screen.getAllByRole("checkbox", { name: "Select row" }) as HTMLInputElement[];

    fireEvent.click(rowBoxes()[0]);
    fireEvent.click(rowBoxes()[2], { shiftKey: true });

    expect(rowBoxes().map((b) => b.checked)).toEqual([true, false, true]);
  });

  it("passes the selected rows to a head action and clears the selection", () => {
    const onBulk = vi.fn((_selected: ITableRow[]) => true);
    const withActions = rows().map((r) => ({
      ...r,
      bodyActions: [{ name: "Skip", onClick: () => false }],
    }));
    renderTable(
      <DataTable
        headCells={headCells}
        rows={withActions}
        withCheckboxes
        headActions={[{ name: "Skip selected", onClick: onBulk }]}
      />,
    );
    const rowBoxes = () =>
      screen.getAllByRole("checkbox", { name: "Select row" }) as HTMLInputElement[];

    fireEvent.click(rowBoxes()[0]);
    fireEvent.click(rowBoxes()[2]);
    fireEvent.click(screen.getByRole("button", { name: "Skip selected" }));

    expect(onBulk).toHaveBeenCalledTimes(1);
    expect(onBulk.mock.calls[0][0].map((r) => r.id)).toEqual(["a", "c"]);
    expect(rowBoxes().some((b) => b.checked)).toBe(false);
  });
});
