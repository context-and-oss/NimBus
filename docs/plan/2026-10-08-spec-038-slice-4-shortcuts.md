# Spec 038 slice 4: Settings links and palette entries

## Context
Slices 1–3 are merged (#218, #219, #231). Spec 038's last slice (§9.2–§9.3 and §10) makes the new pages easy to reach:

- Pages that have a setting link straight to its Settings panel.
- The Ctrl K command palette can jump to pages, operations and settings, not just endpoints, event types, events and sessions.

## Branch
- New branch `claude/manage-shortcuts`, cut from `origin/master`.
- Commit this plan as `docs/plan/2026-10-08-spec-038-slice-4-shortcuts.md`.
- PR against `master`.

## Design

### 1. "⚙" settings links in page headers
Each link is a react-router `<Link>` placed in the `Page` `actions` slot, beside the existing actions, and styled as a small outline button.

| Page | Link | Who sees it |
|---|---|---|
| `pages/heartbeat.tsx` | **⚙ Configure probing** → `/Settings/heartbeat` | site Owners; the page already has `canManageSite` |
| `pages/simulate.tsx` | **⚙ Simulation settings** → `/Settings/simulation` | everyone who reaches the page, which is site Owners only |
| `pages/audits-list.tsx` | **⚙ Configure recording** → `/Settings/audit` | site Owners, via `useAccess()`, newly added to this page |

Both audit test files need a `hooks/use-access` mock: their FakeClient has no `getAccessControlMe`.

### 2. Palette entries for site Owners
- **Kinds:** add `"page"`, `"operation"` and `"setting"` to `PaletteResult.kind` in `components/command-palette/use-palette-search.ts`.
  - The spec named two kinds. A separate **Operations** section reads better than mixing operations into Pages; the spec will be updated.
- **Entries:** built from the `models/manage-pages.ts` registries, client-side and static. No request is added.

  | Section | Entries | Route |
  |---|---|---|
  | **Pages** | Operations, Topology, Settings | `/Operations`, `/Topology`, `/Settings` |
  | **Pages** | the three Topology views ("Topology › Catalog drift") | `/Topology/:view` |
  | **Operations** | each `OPERATIONS` entry ("Bulk resubmit failed") | `/Operations/:id` |
  | **Settings** | each feature ("MCP access") | `/Settings/:id` |
  | **Settings** | each tab ("MCP access › Limits") | `/Settings/:id?tab=:tab` |

  - Matching is a case-insensitive substring over the title plus keywords:
    - operations: label plus description;
    - features: name plus description;
    - tabs: tab label plus the tab's `keywords`, so "rate" finds *MCP access › Limits* and *Simulation › Simulate mode*.
  - Each section holds at most `MAX_LOCAL_RESULTS` (8) entries.
- **Owner gating:** the hook takes a `canManage` flag. `command-palette.tsx` passes it from `useAccess().access?.canManageAccessControl`, so non-Owners see exactly what they see today.
- **Rendering** (`command-palette.tsx`):
  - Add the three kinds to `SECTION_LABELS`, `SECTION_ORDER`, `KIND_ICON` and `grouped`. The icons reuse the sidebar's operations, topology and settings glyphs.
  - The hook pushes results in `SECTION_ORDER` order, so arrow keys follow the visual order. That's an existing invariant (the highlight indexes the flat array).
  - Order: Pages, Operations, Settings, Endpoints, Event types, Events, Sessions. Navigation shortcuts come first.
- **Dropped from spec §10:** the "endpoint X — endpoint controls" entries. `/Operations?endpoint=X` only pre-fills a form; it doesn't focus the table row. Endpoints are already palette results, and their row is one click away on Operations. The spec will be updated.

### 3. Spec
- Status line: slices 1–3 merged; slice 4 on this branch.
- §10: the three kinds, the order, and dropping the endpoint-controls entries.

## Critical files
- `components/command-palette/use-palette-search.ts`, `components/command-palette/command-palette.tsx`
- `pages/heartbeat.tsx`, `pages/simulate.tsx`, `pages/audits-list.tsx`
- Tests: `pages/heartbeat.test.tsx`, `pages/simulate.test.tsx`, `pages/audits-list.*.test.tsx` (add the use-access mock), new `components/command-palette/use-palette-search.test.ts(x)`, new `components/command-palette/command-palette.test.tsx`
- `docs/spec/038-manage-pages-split/spec.md`

## Tests (written first, RED, then GREEN)
- **Header links**
  - Heartbeat: Owners see the link to `/Settings/heartbeat`; others don't.
  - Simulate: the link to `/Settings/simulation` is shown.
  - Audit Log: Owners see the link to `/Settings/audit`; others don't.
- **`usePaletteSearch`**
  - With `canManage`, "rate" returns *MCP access › Limits* and *Simulation › Simulate mode* (setting kind) with their `?tab=` routes.
  - "drift" returns the Topology view.
  - "resubmit" returns the operation with route `/Operations/resubmit`.
  - Without `canManage`, none of these kinds appear.
  - Results come in section order.
  - An empty query still returns nothing.
- **`CommandPalette`**
  - An Owner typing "dead" sees an **Operations** section.
  - Enter navigates to `/Operations/dlq`.
  - ArrowDown moves through the sections in visual order.

## Verification
- `npm run test:ci`, `npm run build` and ESLint on the changed files.
- **Browser:** if Docker is back, use the Aspire stack; otherwise the scratchpad mock API and Vite setup. Check:
  - Ctrl K → "payload", "rate", "dead-letter", "drift": sections, keyboard navigation, navigation targets;
  - the three header links;
  - a non-Owner (mock `canManageAccessControl: false`) sees none of them.
- One PR screenshot of the palette, on `pr-assets/manage-shortcuts`.
