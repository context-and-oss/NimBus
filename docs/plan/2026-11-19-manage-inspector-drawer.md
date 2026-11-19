# Manage pages: full-width tables with an inspector drawer

Operations, Topology, Settings and Access Control keep the workspace layout of
[2026-11-18-manage-workspace-layout.md](2026-11-18-manage-workspace-layout.md), but the
inspector stops being a permanent right-hand column. Like Claude Console's API keys page,
the table takes the full width, a click on a row opens the inspector as a drawer over the
table's right side, and each row carries a ⋯ menu of its actions. Frontend only
(`src/NimBus.WebApp/ClientApp`); no API, storage or authorization change.

## Why

The inspector column took a quarter of the width whether or not a row was selected. Topology's
tables carry eight columns, and an empty column with "Select a row…" in it was the most common
state of every Manage page. The repo owner chose the Console pattern from a clickable mockup
and asked for it on every Manage page.

## Layout

`components/workspace/workspace.tsx` and `selectable-row.tsx`:

| Piece | Behaviour |
|---|---|
| `Workspace` | The table section takes the full width. A click anywhere on a row clicks its `RowSelect` button, unless it lands on a control in the row (toggle, link, ⋯) or ends a text selection. The inspector renders into a fixed drawer at the right edge, 32rem (40rem with `wideInspector`), full width on a phone. |
| `Inspector` | In a workspace it is the drawer: open while it has a title, closed (renders nothing) while it hasn't. Its top bar has the eyebrow, an optional `reference`, ↑/↓ to step to the previous and next visible row, and ✕ (`onClose`). Esc closes it and ↑/↓ step rows, unless a field, menu or modal has the key. Closing returns focus to the selected row. Outside a workspace it renders in place, as before. |
| `useInspectorDrawer()` | True in a workspace. Agent keys and Dashboard tokens show their issue form only after **New key** / **New token** there; standalone they show it always, as before. |
| `RowActions`, `RowActionsCell`, `RowActionsHeader` | A row's ⋯ menu on the existing `DropdownMenu`; destructive items last, in red, after a separator. The inspector takes the same menu through `menu`. |
| `selectableRowClass`, `WORKSPACE_HEAD_ROW` | Rows show a pointer; column headers are sentence case instead of mono uppercase. |

`DropdownMenu` now handles Escape in the capture phase and marks it handled, so Escape closes
an open menu without also closing the drawer behind it (as `Modal` already did).

Stepping uses the DOM: the drawer clicks the neighbouring visible row's `RowSelect` (marked
`data-row-select`), so each page keeps owning its selection and no page passes row lists in.

## Pages

- **Topology → Subscriptions**: topics and subscriptions get ⋯ menus. Topic: Open
  subscriptions, and Delete topic… for one outside the platform. Subscription: Pause/Resume,
  Restore rules, Inspect dead letters, Purge…, Purge dead letters…, Delete & recreate…,
  Delete… — the inspector's actions, through the same confirmations (`subscriptionFacts`
  decides availability for both).
- **Topology → Catalog drift**: closing the drawer clears the endpoint (the page's topic chip
  when controlled). No ⋯ menu: Remove Deprecated needs the endpoint's fresh audit.
- **Operations → Endpoints**: ⋯ lists the operations that start from the endpoint.
- **Operations → Operations**: the selected operation's form opens in the wide drawer; closing
  it keeps the URL and deselects the row, and clicking the row reopens it.
- **Access Control**: the Add grant form opens on **New grant** instead of filling the empty
  column. ⋯ has Open and Remove grant…, which opens the grant with its removal to confirm.
- **Settings**: Agent keys (⋯: Rotate, Revoke… / Remove…) and Dashboard tokens (⋯: Revoke… /
  Remove…) open their issue form on request. MCP Permissions, Heartbeat endpoints, Simulation
  consuming endpoints and Audit action types get a closable drawer.

## Not done

- The Failed and Messages pages (in the mockup) are Observe pages with their own layout and
  event details page; they are not workspaces and are left for a follow-up.
- A drawer form loses unsaved input when closed (Add grant, Issue key). The Settings frame's
  `beforeunload` guard still covers leaving the page.

## Verification

- `npm run test:ci` (1129 tests), `npm run lint` (no new warnings), `npm run build`.
- New tests: drawer open/close/Escape/focus return, ↑/↓ stepping and bounds, arrow keys left to
  fields, row menu without selecting the row, Escape closing only the menu; Topology and Access
  Control row menus; closing an Operations drawer.
- `dotnet build src/NimBus.slnx -c Release`.
- Topology checked in a browser against a mock API, light and dark.
