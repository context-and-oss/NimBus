import { useState, useMemo, useEffect, useRef } from "react";
import { useNavigate } from "react-router-dom";
import {
  useTable,
  tableFeatures,
  columnFilteringFeature,
  columnSizingFeature,
  globalFilteringFeature,
  rowPaginationFeature,
  rowSelectionFeature,
  rowSortingFeature,
  createFilteredRowModel,
  createPaginatedRowModel,
  createSortedRowModel,
  sortFn_alphanumeric,
  sortFn_basic,
  sortFn_datetime,
  sortFn_text,
  flexRender,
  type SortingState,
  type ColumnDef,
  type Row,
  type RowSelectionState,
} from "@tanstack/react-table";
import { cn } from "lib/utils";
import { Spinner } from "components/ui/spinner";
import { Checkbox } from "components/ui/checkbox";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Tooltip } from "components/ui/tooltip";
import { DropdownItem, DropdownMenu } from "components/ui/dropdown-menu";
import type {
  ITableRow,
  ITableHeadCell,
  ITableHeadAction,
  ITableBodyAction,
  SortDirection,
} from "./types";

// Re-export types for backward compatibility
export type {
  ITableRow,
  ITableHeadCell,
  ITableHeadAction,
  ITableBodyAction,
  ITableData,
} from "./types";

// The table features this component uses. Each row model follows its feature,
// and the automatic sort ("auto") only resolves the sort functions registered here.
const features = tableFeatures({
  columnSizingFeature,
  columnFilteringFeature,
  globalFilteringFeature,
  filteredRowModel: createFilteredRowModel(),
  rowSortingFeature,
  sortedRowModel: createSortedRowModel(),
  sortFns: {
    alphanumeric: sortFn_alphanumeric,
    basic: sortFn_basic,
    datetime: sortFn_datetime,
    text: sortFn_text,
  },
  rowPaginationFeature,
  paginatedRowModel: createPaginatedRowModel(),
  rowSelectionFeature,
});

type Features = typeof features;

interface DataTableProps {
  headCells: ITableHeadCell[];
  headActions?: ITableHeadAction[];
  rows: ITableRow[];
  isLoading?: boolean;
  noDataMessage?: string;
  withToolbar?: boolean;
  withCheckboxes?: boolean;
  order?: SortDirection;
  orderBy?: string;
  styles?: React.CSSProperties;
  count?: number;
  onPageChange?: () => void;
  /**
   * True while the server can produce more rows (a continuation token exists).
   * Keeps Next enabled on the last local page — clicking it then invokes
   * onPageChange to fetch the next server page instead of dead-ending, which
   * matters when a client-side filter (e.g. Hide reported) thins the loaded
   * rows below the local page count.
   */
  hasMoreRows?: boolean;
  /**
   * How per-row actions are presented. "buttons" (default) renders one
   * outline button per action inline; "menu" renders a single "…" trigger on
   * rows that have actions, which opens a dropdown anchored to it.
   */
  rowActions?: "buttons" | "menu";
  endpointIds?: string[];
  checkedEndpointIds?: string[];
  checked?: (name: string, state: boolean) => void;
  dataRowsPerPage?: number;
  fixedWidth?: string;
  // Deprecated props (kept for backward compatibility)
  hideDense?: boolean;
  subscribe?: string;
  roleAssignmentScript?: string;
}

export function DataTable({
  headCells,
  headActions,
  rows,
  isLoading = false,
  noDataMessage = "No data available",
  withToolbar = true,
  withCheckboxes = false,
  order = "asc",
  orderBy = "",
  styles,
  count,
  onPageChange,
  hasMoreRows = false,
  rowActions = "buttons",
  endpointIds,
  checkedEndpointIds,
  checked,
  dataRowsPerPage = 20,
  fixedWidth,
}: DataTableProps) {
  const [sorting, setSorting] = useState<SortingState>(
    orderBy ? [{ id: orderBy, desc: order === "desc" }] : [],
  );
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({});
  const [globalFilter, setGlobalFilter] = useState("");
  const clearRowSelection = () => setRowSelection({});

  // Convert Map-based rows to column accessor format
  const columns = useMemo<ColumnDef<Features, ITableRow>[]>(() => {
    const cols: ColumnDef<Features, ITableRow>[] = [];

    // Checkbox column
    if (withCheckboxes) {
      cols.push({
        id: "select",
        header: ({ table }) => (
          <Checkbox
            checked={table.getIsAllPageRowsSelected()}
            // From react-table 9 "some" is also true when all rows are selected.
            indeterminate={
              table.getIsSomePageRowsSelected() &&
              !table.getIsAllPageRowsSelected()
            }
            onChange={table.getToggleAllPageRowsSelectedHandler()}
            aria-label="Select all"
          />
        ),
        cell: ({ row }) => (
          <Checkbox
            checked={row.getIsSelected()}
            onChange={row.getToggleSelectedHandler()}
            onClick={(e) => e.stopPropagation()}
            aria-label="Select row"
          />
        ),
        size: 36,
        enableSorting: false,
      });
    }

    // Data columns
    headCells.forEach((cell) => {
      cols.push({
        id: cell.id,
        accessorFn: (row) => row.data.get(cell.id)?.searchValue ?? "",
        header: () =>
          cell.info ? (
            <span className="inline-flex items-center gap-1">
              {cell.label}
              <Tooltip content={cell.info} position="bottom">
                <InfoIcon />
              </Tooltip>
            </span>
          ) : (
            cell.label
          ),
        cell: ({ row }) => {
          const cellData = row.original.data.get(cell.id);
          return (
            <span className="block truncate">{cellData?.value ?? ""}</span>
          );
        },
        size: typeof cell.width === "number" ? cell.width : undefined,
        enableSorting: true,
      });
    });

    // Actions column
    if (rows.some((row) => row.bodyActions && row.bodyActions.length > 0)) {
      cols.push({
        id: "actions",
        header: () =>
          headActions && headActions.length > 0 ? (
            <div className="flex justify-end gap-2">
              {headActions.map((action, i) => (
                <Button
                  key={i}
                  size="xs"
                  variant="outline"
                  disabled={!rows.some((r) => rowSelection[r.id])}
                  onClick={(e) => {
                    e.preventDefault();
                    e.stopPropagation();
                    // Selection is keyed by stable row id (getRowId below), so
                    // filtering/reordering the rows prop can never redirect an
                    // action to a different event. Hidden-but-selected rows are
                    // excluded because they are absent from `rows`.
                    const selectedRows = rows.filter((r) => rowSelection[r.id]);
                    try {
                      action.onClick(selectedRows);
                    } finally {
                      clearRowSelection();
                    }
                  }}
                >
                  {action.name}
                </Button>
              ))}
            </div>
          ) : null,
        cell: ({ row }) => {
          const actions = row.original.bodyActions;
          if (!actions || actions.length === 0) return null;
          if (rowActions === "menu") {
            return (
              <div className="flex justify-end">
                <RowActionsMenu actions={actions} onDone={clearRowSelection} />
              </div>
            );
          }
          return (
            <div className="flex justify-end gap-2">
              {actions.map((action, i) => (
                <Button
                  key={i}
                  size="xs"
                  variant="outline"
                  onClick={(e) => {
                    e.preventDefault();
                    e.stopPropagation();
                    try {
                      action.onClick();
                    } finally {
                      clearRowSelection();
                    }
                  }}
                >
                  {action.name}
                </Button>
              ))}
            </div>
          );
        },
        enableSorting: false,
      });
    }

    return cols;
  }, [headCells, withCheckboxes, headActions, rows, rowSelection, rowActions]);

  // Custom global filter function for Map-based data
  const globalFilterFn = useMemo(
    () => (row: ITableRow, filterValue: string) => {
      if (!filterValue) return true;
      const searchLower = filterValue.toLowerCase();
      const data = Array.from(row.data.values());
      return data.some((cell) =>
        cell.searchValue.toString().toLowerCase().includes(searchLower),
      );
    },
    [],
  );

  const table = useTable({
    features,
    data: rows,
    columns,
    state: {
      sorting,
      rowSelection,
      globalFilter,
    },
    // Stable identity: selection state is keyed by the caller's row id, not the
    // array index — indices shift when rows are filtered (e.g. Hide reported)
    // and index-keyed selection would then target the wrong rows.
    getRowId: (row) => row.id,
    enableRowSelection: withCheckboxes,
    // From react-table 9 Shift+click selects a range; keep single-row toggling.
    enableRowRangeSelection: false,
    onSortingChange: setSorting,
    onRowSelectionChange: setRowSelection,
    onGlobalFilterChange: setGlobalFilter,
    globalFilterFn: (row, columnId, filterValue) =>
      globalFilterFn(row.original, filterValue),
    initialState: {
      pagination: {
        pageIndex: 0,
        pageSize: dataRowsPerPage,
      },
    },
  });

  // Prefetch the next page when the user actually advances pageIndex. The previous
  // version also fired on `rows.length` changes, which produced a cascade: an initial
  // fetch of `pageSize` rows immediately satisfied the "≤ one page from the end"
  // condition, triggered onPageChange, that appended more rows, which fired the effect
  // again, etc. — so opening a list page issued 2–3 fetches without user input.
  const prevPageIndex = useRef(0);
  const pageIndex = table.state.pagination.pageIndex;
  useEffect(() => {
    if (pageIndex <= prevPageIndex.current) {
      prevPageIndex.current = pageIndex;
      return;
    }
    prevPageIndex.current = pageIndex;
    if (!onPageChange) return;
    const pageSize = table.state.pagination.pageSize;
    if (rows.length - pageSize * (pageIndex + 1) <= pageSize) {
      onPageChange();
    }
  }, [pageIndex, onPageChange, rows.length, table]);

  const selectedRows = useMemo(() => {
    return rows.filter((r) => rowSelection[r.id]);
  }, [rows, rowSelection]);

  return (
    <div className="w-full" style={styles}>
      {/* Toolbar */}
      {withToolbar && (
        <div className="flex items-center justify-between px-4 py-3">
          <div className="w-1/2">
            <Input
              type="text"
              placeholder="Search..."
              value={globalFilter}
              onChange={(e) => setGlobalFilter(e.target.value)}
              leftElement={
                <svg
                  className="w-4 h-4"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
                  />
                </svg>
              }
            />
          </div>
          {endpointIds && checked && (
            <EndpointFilter
              endpointIds={endpointIds}
              checkedEndpointIds={checkedEndpointIds}
              checked={checked}
            />
          )}
        </div>
      )}

      {/* Table */}
      <div className="overflow-auto bg-card border border-border rounded-nb-md">
        <table
          className="w-full text-[13px]"
          style={
            fixedWidth ? { tableLayout: "fixed", width: fixedWidth } : undefined
          }
        >
          <thead className="bg-muted sticky top-0 z-10 border-b border-border">
            {table.getHeaderGroups().map((headerGroup) => (
              <tr key={headerGroup.id}>
                {headerGroup.headers.map((header) => (
                  <th
                    key={header.id}
                    className={cn(
                      "px-3.5 py-3 text-left font-semibold text-muted-foreground",
                      "text-[11px] uppercase tracking-[0.06em] whitespace-nowrap",
                      "bg-muted border-b border-border",
                      header.column.getCanSort() &&
                        "cursor-pointer select-none hover:text-foreground",
                    )}
                    style={{
                      width:
                        header.column.id === "select"
                          ? "36px"
                          : header.getSize() !== 150
                            ? header.getSize()
                            : undefined,
                    }}
                    onClick={header.column.getToggleSortingHandler()}
                  >
                    <div className="flex items-center gap-1">
                      {header.isPlaceholder
                        ? null
                        : flexRender(
                            header.column.columnDef.header,
                            header.getContext(),
                          )}
                      {header.column.getCanSort() && (
                        <SortIcon direction={header.column.getIsSorted()} />
                      )}
                    </div>
                  </th>
                ))}
              </tr>
            ))}
          </thead>
          <tbody className="divide-y divide-border">
            {isLoading ? (
              <tr>
                <td colSpan={columns.length} className="py-8">
                  <div className="flex justify-center">
                    <Spinner size="lg" />
                  </div>
                </td>
              </tr>
            ) : table.getRowModel().rows.length === 0 ? (
              <tr>
                <td
                  colSpan={columns.length}
                  className="px-4 py-8 text-muted-foreground"
                >
                  {noDataMessage}
                </td>
              </tr>
            ) : (
              table.getRowModel().rows.map((row) => {
                const rowData = row.original;
                return <TableRow key={row.id} row={row} rowData={rowData} />;
              })
            )}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      <div className="flex items-center justify-between px-4 py-3 border-t border-border">
        <div className="text-sm text-muted-foreground">
          {table.getFilteredRowModel().rows.length > 0 && (
            <>
              Showing{" "}
              {table.state.pagination.pageIndex *
                table.state.pagination.pageSize +
                1}{" "}
              to{" "}
              {Math.min(
                (table.state.pagination.pageIndex + 1) *
                  table.state.pagination.pageSize,
                table.getFilteredRowModel().rows.length,
              )}{" "}
              of {count ?? table.getFilteredRowModel().rows.length} results
            </>
          )}
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => table.previousPage()}
            disabled={!table.getCanPreviousPage()}
          >
            Previous
          </Button>
          <span className="text-sm text-muted-foreground">
            Page {table.state.pagination.pageIndex + 1} of{" "}
            {table.getPageCount()}
          </span>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              if (table.getCanNextPage()) {
                table.nextPage();
              } else {
                // Last local page but the server has more — fetch the next
                // server page; the appended rows extend the current page (or
                // create the next one).
                onPageChange?.();
              }
            }}
            disabled={!table.getCanNextPage() && !hasMoreRows}
          >
            Next
          </Button>
        </div>
      </div>
    </div>
  );
}

// "…" trigger + dropdown anchored to it listing a row's actions. The menu is
// portaled to document.body, but React synthetic events still bubble through
// the component tree — so the wrapper stops click/auxclick propagation,
// otherwise choosing an action would also fire the row's navigation.
function RowActionsMenu({
  actions,
  onDone,
}: {
  actions: ITableBodyAction[];
  onDone: () => void;
}) {
  return (
    <span
      className="inline-flex"
      onClick={(e) => e.stopPropagation()}
      onAuxClick={(e) => e.stopPropagation()}
    >
      <DropdownMenu
        trigger={<span className="text-lg leading-none">⋯</span>}
        triggerLabel="Actions"
        triggerClassName="h-7 w-8"
        menuClassName="min-w-[150px]"
      >
        {actions.map((action) => (
          <DropdownItem
            key={action.name}
            title={action.description}
            onSelect={() => {
              try {
                action.onClick();
              } finally {
                onDone();
              }
            }}
          >
            {action.name}
          </DropdownItem>
        ))}
      </DropdownMenu>
    </span>
  );
}

// Info icon for header-cell help tooltips
function InfoIcon() {
  return (
    <svg
      className="w-3.5 h-3.5 text-muted-foreground cursor-help"
      fill="none"
      stroke="currentColor"
      viewBox="0 0 24 24"
      aria-hidden="true"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        strokeWidth={2}
        d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
      />
    </svg>
  );
}

// Sort icon component
function SortIcon({ direction }: { direction: false | "asc" | "desc" }) {
  if (!direction) {
    return (
      <svg
        className="w-4 h-4 text-muted-foreground"
        fill="none"
        stroke="currentColor"
        viewBox="0 0 24 24"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          strokeWidth={2}
          d="M7 16V4m0 0L3 8m4-4l4 4m6 0v12m0 0l4-4m-4 4l-4-4"
        />
      </svg>
    );
  }
  return (
    <svg
      className={cn("w-4 h-4", direction === "asc" ? "rotate-180" : "")}
      fill="none"
      stroke="currentColor"
      viewBox="0 0 24 24"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        strokeWidth={2}
        d="M19 9l-7 7-7-7"
      />
    </svg>
  );
}

// Open a row's route in a new browser tab. No window-features string -> the
// browser opens a tab (a features string would force a popup window instead).
// Null out `opener` to avoid reverse-tabnabbing.
function openRowInNewTab(route: string) {
  const opened = window.open(route, "_blank");
  if (opened) opened.opener = null;
}

// True when the click landed on an interactive element inside the row —
// row-level navigation must not hijack those.
function isInteractiveTarget(e: React.MouseEvent): boolean {
  const target = e.target as HTMLElement;
  return !!target.closest(
    'input, button, a, [role="button"], [role="checkbox"]',
  );
}

// Table row component with proper navigation handling
function TableRow({
  row,
  rowData,
}: {
  row: Row<Features, ITableRow>;
  rowData: ITableRow;
}) {
  const navigate = useNavigate();

  const handleRowClick = (e: React.MouseEvent) => {
    if (!rowData.route || isInteractiveTarget(e)) return;
    // Ctrl (Win/Linux) or Cmd (macOS) click -> open in a new tab, like a
    // real link.
    if (e.ctrlKey || e.metaKey) {
      openRowInNewTab(rowData.route);
      return;
    }
    navigate(rowData.route);
  };

  // Middle-click never fires onClick; handle it here. Guard button === 1 so a
  // right-click (context menu) is left alone.
  const handleRowAuxClick = (e: React.MouseEvent) => {
    if (!rowData.route || e.button !== 1 || isInteractiveTarget(e)) return;
    e.preventDefault();
    openRowInNewTab(rowData.route);
  };

  return (
    <tr
      className={cn(
        "group hover:bg-primary-tint dark:hover:bg-primary/15 transition-colors",
        rowData.tone === "reported" && "bg-green-50/60 dark:bg-green-900/15",
        row.getIsSelected() && "bg-primary-50 dark:bg-primary/10",
        rowData.route && "cursor-pointer",
      )}
      onClick={rowData.route ? handleRowClick : undefined}
      onAuxClick={rowData.route ? handleRowAuxClick : undefined}
      title={rowData.hoverText}
    >
      {row.getAllCells().map((cell) => (
        <td
          key={cell.id}
          className={cn(
            "px-3.5 py-2.5 border-b border-border",
            "text-foreground tabular-nums",
            cell.column.id === "select" && "w-9",
          )}
        >
          {flexRender(cell.column.columnDef.cell, cell.getContext())}
        </td>
      ))}
    </tr>
  );
}

// Endpoint filter component (simplified)
function EndpointFilter({
  endpointIds,
  checkedEndpointIds,
  checked,
}: {
  endpointIds: string[];
  checkedEndpointIds?: string[];
  checked: (name: string, state: boolean) => void;
}) {
  const [isOpen, setIsOpen] = useState(false);

  return (
    <div className="relative">
      <Button variant="outline" size="sm" onClick={() => setIsOpen(!isOpen)}>
        Filter Endpoints
        <svg
          className="w-4 h-4 ml-1"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={2}
            d="M19 9l-7 7-7-7"
          />
        </svg>
      </Button>
      {isOpen && (
        <div className="absolute right-0 mt-2 w-64 bg-popover text-popover-foreground rounded-lg shadow-lg border border-border z-20 max-h-64 overflow-auto">
          <div className="p-2">
            {endpointIds.map((id) => (
              <label
                key={id}
                className="flex items-center gap-2 px-2 py-1 hover:bg-accent rounded-sm cursor-pointer"
              >
                <input
                  type="checkbox"
                  checked={checkedEndpointIds?.includes(id) ?? false}
                  onChange={(e) => checked(id, e.target.checked)}
                  className="rounded-sm border-input text-primary focus:ring-primary"
                />
                <span className="text-sm truncate">{id}</span>
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// Default export for backward compatibility
export default DataTable;
