# Manage pages: workspace with table and inspector

Operations, Topology, Settings and Access Control move to one layout: a chip bar, a header with
figures and tabs, a table that takes the width, and an inspector on the right for the selected
row. Frontend only (`src/NimBus.WebApp/ClientApp`); no API, storage or authorization change.

## Why

#425 and #426 gave Settings, Access Control and Topology a split view: a 17rem list on the left
and the chosen item on the right. On Topology the list took width from a subscriptions table
that already carried eight columns and up to seven buttons per row. The design canvas compared
that layout (A) with a workspace (B), and the repo owner chose B for every Manage page.

Spec 038 still defines the pages' routes, access rules and each feature's save rules. Its
§6.3 (operation list), §7 (Settings list) and §8 (drawer) describe the layout this replaces.

## Layout

`components/workspace/workspace.tsx` and `selectable-row.tsx`:

| Piece | Role |
|---|---|
| `Workspace` | The page frame: chips, header (title, status, figures, actions, tabs), table section, inspector column, sticky footer. From `xl` up the inspector stays in view while the page scrolls; below that it follows the table. |
| `WorkspaceChip`, `WorkspaceStat`, `WorkspaceTabs`, `WorkspaceSection` | The chip bar's choices, the header's figures (shortcuts when clickable), the tab strip, and the titled section around a table. |
| `Inspector`, `InspectorTiles`, `InspectorList`, `InspectorBlock` | The selected row's details and controls, with an empty state. |
| `InspectorPortal` | Lets the component that owns a selection fill the inspector from where it renders. Outside a workspace it renders in place, so components still work and test on their own. |
| `RowSelect`, `selectableRowClass` | A row's first cell holds the `aria-pressed` button that selects it; the selected row is tinted and marked on its leading edge. |

Colours come from the theme tokens, so dark mode works.

## Pages

URLs are unchanged: `/Operations/:operation?endpoint=`, `/Topology/:view?topic=`,
`/Settings/:feature?tab=`, `/AccessControl`.

### Operations

- Two tabs. **Endpoints** (`/Operations`) is the kill switch and backlog table. **Operations**
  (`/Operations/:operation`) lists the bulk operations by blast radius.
- Header figures: Failed, Dead-lettered, Paused, Pending, each a shortcut as before. The
  Endpoints table stays mounted, hidden, on the Operations tab so Paused stays loaded.
- Endpoints: chips All endpoints / Needs attention. The inspector shows the selected endpoint's
  counts, its Receive and Send switches, and the operations that start from it. That list
  replaces the row's "Resubmit…" button and "⋯" menu.
- Operations: chips filter by group. The inspector hosts the selected operation's existing card.
  The cards' grids use container queries, so they stack at the inspector's width.

### Topology

- Chips: All topics and one per topic, marked when a topic is disabled, outside the platform or
  a system topic. Header figures: Topics, Subscriptions, Drift (known after "Audit all
  endpoints"). AsyncAPI export stays a header action.
- Subscriptions, all topics: the sortable topics table. Selecting a topic shows its counts in
  the inspector with **Open subscriptions**, and **Delete topic** for one outside the platform.
  Subscriptions load per topic, so there is no table of every subscription.
- Subscriptions, one topic: the table lists and counts. Pause/Resume, Restore rules, Inspect
  dead letters, Purge, Purge dead letters, Delete & recreate, Delete and the detachable rule
  chips are in the inspector, with the same names and confirmations. An action's outcome shows
  in the inspector, or above the table once its subscription is gone.
- Catalog drift: the by-endpoint table is the table; the inspector shows the selected
  endpoint's subscriptions and rules and **Remove Deprecated**.
- Storage (Cosmos DB only) keeps its multi-select table and has no inspector.

### Settings

- No list page. The seven features are chips with their live state; `/Settings` shows the
  first. The header carries the open feature's status, description, quick action (Send now,
  Open Simulate) and, for MCP access, the 24-hour figures. MCP access keeps its own Turn off now.
- `SettingsPanelFrame` renders the workspace. `PanelSection`, `PanelFooter`, `usePanelDirty`
  and `usePanelReviewing` keep their contracts; `PanelInspector` is new. The close button and
  Escape-to-close are gone, since there is nothing to close to. The unsaved-changes prompt
  guards switching feature, and `beforeunload` still guards leaving the page.
- Tabs that are lists are tables with an inspector (`inspector: true` in
  `models/manage-pages.ts`): MCP Permissions, Agent keys, Dashboard tokens, Heartbeat
  Endpoints, Simulation Consuming endpoints, Audit Action types.
- Form tabs keep their forms at full width: MCP Overview, Who & where, Limits, Connect and
  Activity; all of Failure intelligence; Heartbeat Probing; Simulate mode; Environment policy.
- Save, review, widening confirmation and consent are untouched.

### Access Control

- Chips: Site (site Owners) and one per endpoint the user may manage. Grants load per scope, so
  there is no "All" chip. The header says who is signed in and with which roles.
- The grants table has Principal, Type, Role and Scope. The inspector shows the selected grant
  with its removal, or the Add grant form when nothing is selected; **New grant** returns to
  the form.

## Not done

- Form-style Settings tabs are not broken into one row per setting, as the canvas draws them.
  That rewrites every settings component and its save flow, and is left for a follow-up.
- The filter box and On / Off / Needs attention filter of the old Settings list are gone with
  the list. The command palette still finds every feature and tab.
- Topic and scope chips navigate with buttons, not links, so they can't be opened in a new tab.

## Verification

- `npm --prefix src/NimBus.WebApp/ClientApp run test:ci`, `run lint` and `run build`.
- `dotnet build src/NimBus.slnx -c Release`.
- The four pages checked in a browser at desktop and narrow widths, light and dark.
