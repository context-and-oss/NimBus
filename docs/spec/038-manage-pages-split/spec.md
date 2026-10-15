# Spec 038 — Split Admin into Operations, Topology and Settings

| | |
|---|---|
| **Status** | Approved 2026-10-07 (option B). Slices 1–3 merged (context-and-oss/NimBus#218, #219, #231; plans for [slice 2](../../plan/2026-10-08-spec-038-slice-2-settings-panel.md) and [slice 3](../../plan/2026-10-08-spec-038-slice-3-operations.md)). Slice 4 implemented on branch `claude/manage-shortcuts` ([plan](../../plan/2026-10-08-spec-038-slice-4-shortcuts.md)). §13's open decisions take the proposed defaults unless the repo owner says otherwise. |
| **Date** | 2026-10-07 |
| **Baseline** | master `0f3b02ef` |
| **Mockup** | [mockup.html](mockup.html). Open it in a browser. The yellow **Design notes** cite the sections below. The **Mock state** bar switches between the states in §7.3. [alternative-a.html](alternative-a.html) is the rejected alternative in §15. |
| **Scope** | `NimBus.WebApp/ClientApp` only: routes, sidebar, three new pages, a settings drawer, and moving the existing `components/admin/*` into them. No API, storage or authorization changes. A few user-facing strings in `NimBus.WebApp` and `NimBus.AppHost` that name "Admin → …" are reworded (§12.2). |

The Admin page has grown to eight tabs, nine on Cosmos DB. They mix three unrelated jobs: acting on messages, inspecting the Service Bus namespace, and switching optional features on and off. This spec removes the Admin page. Its content moves to three pages in the sidebar's **Manage** group, one per job:

- **Operations** (`/Operations`): the endpoint kill switch and every bulk message operation.
- **Topology** (`/Topology`): Service Bus subscriptions, catalog drift, AsyncAPI export and Cosmos DB containers.
- **Settings** (`/Settings`): one row per optional feature with its live state. Each feature is configured in a side panel.

---

## 1. Problems with today's Admin page

1. **Too many tabs, all treated the same.** `pages/admin.tsx:24-34` renders Topology, Operations, Subscriptions, Health, Storage (Cosmos only), Failure intelligence, Simulation, Audit and MCP access as equal-width tabs. The tab strip already fills the content width at 1440 px. The next feature has nowhere to go.
2. **Unrelated jobs side by side.** A Danger-zone delete sits one tab away from a read-only AsyncAPI export and an AI-provider API key.
3. **No URLs.** Tabs are selected by index (`<Tab index={5}>`). A tab cannot be linked or bookmarked, and a refresh always returns to Topology. Runbooks have to say "open Admin, click the sixth tab". `failure-intelligence-settings.tsx:117` works around this with a hard-coded `← Back to Admin` link on phones.
4. **The wrong default.** Topology is tab 0, so an operator who opens Admin during an incident lands on a catalog export.
5. **The kill switch is buried.** The endpoint kill switch is the fourth accordion group inside the Operations tab (`components/admin/operations.tsx`), below eight bulk-operation forms.
6. **Feature state is invisible.** You cannot tell whether MCP access, Failure intelligence or Simulation is on without opening each tab.
7. **Health mixes two things.** `components/admin/health.tsx` holds *Platform services* (live status) and *Heartbeat* configuration: which endpoints are probed, the interval and timeout, and "Send now" (`heartbeat-card.tsx:305-321`). The live heartbeat view is already its own page, `/Heartbeat`.

## 2. Facts this design relies on

All verified against the baseline:

- **Access.** Admin is shown only to site Owners (`components/sidebar.tsx:308`). Every `/api/admin/*` operation checks `IsSiteOwnerAsync()` on the server (`Controllers/ApiContract/AdminImplementation.cs`). The same gate applies to the new pages, and the server remains authoritative.
- **The settings components have different save rules.** A shared drawer must not flatten them (§8.3):

  | Feature | When it applies | Extra confirmation |
  |---|---|---|
  | MCP access | Every instance within 30 s, no restart | Review lists *widens/narrows*; a widening change needs `confirmWidening` |
  | Failure intelligence | Saves a shared revision; needs a restart of every instance | Payload consent checkbox; the API key is write-only |
  | Simulation | At once, but only on this instance; resets on restart | Taking ownership of an endpoint has its own confirmation |
  | Audit logging | At once | Discard / Save |
  | Heartbeat probing | On the next probe | No review step today |
- **Drawer component.** `components/ui/side-panel.tsx` already provides a right-anchored dialog with a focus trap, Escape and backdrop close, and focus return. `pages/failed-messages.tsx` uses it.
- **Sidebar badge data.** The sidebar already polls `getEndpointStatusCountAll` for the Failed badge (`sidebar.tsx`, `useFailedBacklog`). Each row carries `subscriptionStatus`. `null` means unknown (`EndpointImplementation.cs:939-970`). *Send* status is not in that payload; it needs a per-endpoint `getEndpointSendstatus` call.
- **Command palette.** It indexes four kinds of result: `endpoint`, `eventType`, `event` and `session` (`components/command-palette/use-palette-search.ts:17`). It has no page or setting entries.
- **Nothing outside the WebApp navigates to `/Admin`.** No Playwright suite under `samples/` does.

## 3. Goals

- Each page answers one question. Operations: *what do I do about this now?* Topology: *what does the namespace look like?* Settings: *what is switched on, and how is it set up?*
- Every view, operation and setting has a URL.
- Every optional feature's state is visible without opening it.
- Adding a feature adds a Settings row, not a tab.
- Reuse today's components: move them, don't rewrite them. AGENTS.md asks for the WebApp to be enhanced incrementally.

## 4. Non-goals

- No new APIs or authorization model. Endpoint Owners still cannot use these pages (§13, open decision 3).
- No change to any feature's settings semantics, validation or storage.
- No redesign of the forms inside each operation card. They move as they are.
- Access Control and Simulate stay where they are.

## 5. Information architecture

### 5.1 Sidebar

```
MANAGE
  ↻ Operations     [1 paused]   ← site Owner
  ◇ Topology                    ← site Owner
  ⚙ Settings                    ← site Owner
  ▷ Simulate       [running]    ← unchanged: shown only when simulation is allowed and enabled
  ⊡ Access Control              ← unchanged
```

The **Operations** badge counts endpoints whose `subscriptionStatus` reports receive disabled. The data comes from the status-count call the sidebar already makes, so it costs nothing extra. `null` (unknown) never counts. Send-disabled endpoints are not counted, because send status would need N extra calls on every poll. The Operations page shows them (§6.2).

The Manage group grows from three items to five for site Owners: Admin is replaced by three pages. Observe has eight items, so this stays within the sidebar's current length. Collapsing the Manage group is a possible follow-up (§13).

### 5.2 Routes

| Route | View |
|---|---|
| `/Operations` | Operations page, default operation `resubmit` |
| `/Operations/:operation?endpoint=:id` | Operations page with that operation selected and its endpoint pre-filled |
| `/Topology` → `/Topology/subscriptions` | Subscriptions view |
| `/Topology/drift` | Catalog drift (today's Topology audit) |
| `/Topology/storage` | Cosmos DB containers; the route and segment exist only when the provider is Cosmos DB |
| `/Settings` | Settings list |
| `/Settings/:feature?tab=:tab` | Settings list with that feature's drawer open on that tab |
| `/Admin` | Redirects to `/Operations` (§12.1) |

`:operation` ∈ `resubmit, skip, session, stale, dlq, status, to, single, purge, copy, all`.
`:feature` ∈ `mcp, failure-intelligence, heartbeat, simulation, audit`. (The mockup uses short ids.)

### 5.3 Where each part of today's Admin page goes

| Today (tab → section) | New home |
|---|---|
| Topology → Catalog export | Topology page header: **Export AsyncAPI ▾** (YAML / JSON) |
| Topology → Topology audit | Topology → **Catalog drift** |
| Subscriptions | Topology → **Subscriptions** |
| Storage (Cosmos only) | Topology → **Storage** (Cosmos only) |
| Operations → Recovery / Cleanup / Infrastructure | Operations → operation list (§6.3) |
| Operations → Endpoint kill switch | Operations → **Endpoints** table (§6.2) |
| Operations → Danger zone | Operations → operation list, **Irreversible** group |
| Health → Platform services | **Heartbeat** page, site Owners only (§9.1) |
| Health → Heartbeat configuration | Settings → **Heartbeat probing** |
| Failure intelligence | Settings → AI & agents |
| MCP access | Settings → AI & agents |
| Simulation | Settings → Testing |
| Audit | Settings → Compliance |

## 6. Operations page

### 6.1 Status strip

There are four tiles. Each one is also a shortcut:

| Tile | Value | Click |
|---|---|---|
| Failed | Sum of failed counts across endpoints | `/Operations/resubmit` |
| Dead-lettered | Sum of dead-letter counts | `/Operations/dlq` |
| Endpoints paused | Endpoints with receive or send off | Filters the Endpoints table to *Needs attention* |
| Pending | Sum of pending counts | `/Operations/stale` |

All values come from `getEndpointStatusCountAll`, except the paused count, which also uses the send status loaded for §6.2.

### 6.2 Endpoints table

This replaces `EndpointControlsCard` and is the first thing on the page, because it is what an operator reaches for in an incident.

- **Columns:** Endpoint, Receive (toggle), Send (toggle), Failed, Dead-letter, Pending, and row actions.
- **Toggles** call the same APIs as today (`postEndpointSubscriptionstatus`, `postEndpointSendstatus`) and keep today's confirmation. Pausing *send* shows the existing quarantine warning: events forwarded in dead-letter at the source. A paused receive shows "Paused". It can't say for how long: `EndpointMetadata` records only the flag, not when it changed.
- **Row actions:** *Resubmit…* (shown when failed or dead-lettered is above zero) and *⋯* for the other operations. Each one navigates to `/Operations/:operation?endpoint=:id`, which pre-fills that endpoint.
- **Filtering:** a text box plus a **Needs attention** chip. The chip shows only endpoints with a pause, a failure or a dead letter. Endpoints that need attention get a warning-coloured stripe on the left.

### 6.3 Operation list and detail

The list is grouped by blast radius, keeping today's colours and captions:

| Group | Operations | Caption |
|---|---|---|
| Recovery (green) | Bulk resubmit failed · Skip messages · Session purge · Reconcile stale pending | safe · reversible |
| Cleanup (amber) | Delete dead-lettered · Delete by status · Delete by To field · Delete single event | not replayed |
| Data movement (blue) | Purge subscription · Copy endpoint data | cross-environment |
| Irreversible (red) | Delete all events | audit-logged |

"Infrastructure" is renamed **Data movement**, because subscription *management* moved to Topology. Only one operation's form is shown at a time. The detail pane renders today's card components (`BulkResubmitCard`, `SkipMessagesCard`, …) unchanged, apart from accepting an `initialEndpoint` prop. Below 900 px the list collapses into a select above the detail pane.

### 6.4 Safety rules

- Every operation previews its scope before it runs, as the cards already do.
- **Every** operation asks for a typed confirmation (`ConfirmDestructiveAction`):
  - resubmit, skip and reconcile with the endpoint name;
  - Session purge with its own text;
  - Delete by To field with the To value;
  - Delete single event with the event ID.
- **Correction (slice 3):** Delete single event used to delete on one click. Typed confirmation was added in slice 3, at the repo owner's request (2026-10-08).
- **Button colour and labels:** each confirmation names its action, e.g. "Resubmit", "Delete dead-lettered", "Purge subscription" (it used to read "Permanently messages").
  - Resubmit, skip and reconcile replay messages, so they use a primary button with no "cannot be undone" warning.
  - Every other operation stays red. Session purge is red too, although it sits in Recovery: it deletes the session's messages and stored events.
- Progress uses the existing `OperationProgress`. The result links to the Audit Log.

## 7. Settings page

### 7.1 Rows

A small registry (`settings/feature-registry.ts`) describes each feature: id, group, icon, name, one-line description, keyword index, a status hook, a summary hook, a call to action, a quick action, and its drawer tabs. The list renders from that registry. Adding a feature means adding one registry entry and one drawer component.

| Group | Feature | Status (example) | Summary (example) | Row link | Quick action |
|---|---|---|---|---|---|
| AI & agents | MCP access | ● Serving / ○ Off / Not deployed | 2 actions · 0 refused · 24 h | **Turn on ›** (when off) | **Turn off now** (when serving) |
| AI & agents | Failure intelligence | ● On / ○ Off / Restart required | No API key configured | **Set up ›** / **Turn on ›** / **Fix key ›** | — |
| AI & agents | Agent keys | 2 active / No active keys | Agents can't call the agent API where sign-in is required | **Issue key ›** (when none active) | — |
| Monitoring | Heartbeat probing | ● On | 8 of 12 endpoints · every 5 min | Configure › | **Send now** |
| Testing | Simulation | ● On / ○ Off / Blocked in production | Allowed in dev · auto-stop 30 min | **Enable ›** (when off) | **Open Simulate** (when on) |
| Compliance | Audit logging | 14 of 16 recorded | Access-denied and settings changes always recorded | Configure › | — |

Every row is one link into its drawer. It reads **Configure ›** unless the feature's state calls for a next step; then the link names that step and opens the drawer on the tab where it happens. A row never shows a second control that opens the same drawer.

Status and summary reuse the data each settings component already loads. Examples: MCP's `/api/admin/mcp/activity`, Failure intelligence's `state.active` and `restartRequired`, and Simulation's `useSimulationStatus`. The Settings page loads these summaries itself. It does not mount every drawer.

### 7.2 The quick-action rule

A row may act **inline only** when the action narrows access *and* applies at once with no consent, key or restart:

- **Allowed inline:** MCP **Turn off now**, which calls `/api/admin/mcp/turn-off` behind today's confirmation; Heartbeat **Send now**; Simulation **Open Simulate**.
- **Becomes the row link's label instead:** anything that widens access, needs a key or consent, or needs a restart. The link opens the drawer, where the feature's own review and confirmation run. For example: MCP *Turn on ›* opens Overview, Failure intelligence *Set up ›* opens Activation & provider, Agent keys *Issue key ›* opens Keys, and Simulation *Enable ›* opens Simulate mode.

The first mockup (Alternative B v1) put plain on/off toggles on each row. That would have bypassed MCP's widening confirmation and Failure intelligence's key and consent checks, so this spec drops them.

### 7.3 States

Each state comes from data the component already exposes. The mockup's **Mock state** bar shows them.

| State | Row | Drawer |
|---|---|---|
| MCP not deployed (`available` false) | Badge "Not deployed"; no quick action | Info banner. The Serve toggle is disabled, as today. |
| Failure intelligence restart pending (`restartRequired`) | Amber "Restart required"; counted by the *Needs attention* chip | Warning banner, as today |
| Failure intelligence key missing | "No API key configured" | Info banner pointing to the Provider tab |
| Failure intelligence key unreadable (`savedApiKey === "unreadable"`) | Red "Key unreadable" | Today's alert |
| Simulation blocked (`allowed` false) | Red "Blocked in production"; no quick action | Danger banner; controls read-only |

### 7.4 Filter

- **Chips:** All / On / Off / Needs attention. *Needs attention* means a warning or danger status.
- **Text filter:** matches name, description and the registry's keyword index, which lists the labels inside each drawer. So "payload" finds MCP access *and* Failure intelligence, and "rate" finds MCP Limits and Simulation's rate ceiling.

## 8. Settings drawer

### 8.1 Anatomy

The drawer is `SidePanel`, about 880 px wide, and full-screen below 820 px. The full-screen layout replaces Failure intelligence's own `max-sm:fixed` overlay and its `← Back to Admin` link.

```
┌ header ───────────────────────────────────────────────┐
│ Title  [status badge]          [quick action]   [✕]  │
│ revision / applies-when line (mono, muted)            │
│ one-paragraph lead                                    │
│ Tab · Tab • · Tab · Tab        (• = unsaved changes)  │
├ body (scrolls) ───────────────────────────────────────┤
│ state banner (§7.3), then the tab's cards             │
├ footer (sticky) ──────────────────────────────────────┤
│ "2 unsaved changes · 1 widens access"  [Discard] [Review & save] │
└───────────────────────────────────────────────────────┘
```

### 8.2 Tabs for each feature

Each tab maps to today's numbered cards. The `01 · …` prefixes are kept for now. Inside the panel every tab is a single column, so previews such as *Effective access* sit below their controls rather than beside them (slice 2 decision, 2026-10-08).

| Feature | Tabs (source) |
|---|---|
| MCP access | Overview (KPI tiles + 01 Activation) · Permissions (02 + *Effective access* preview beside it) · Who & where (03) · Limits (04) · Connect · Activity |
| Failure intelligence | Activation & provider (01) · Evidence (02 + *What leaves NimBus* preview beside it) · Scope & guardrails (03) · How failures are classified (04) |
| Heartbeat probing | Probing (interval, timeout, Send now) · Endpoints (include/exclude) |
| Simulation | Simulate mode (01) · Consuming endpoints (02) · Environment policy |
| Audit logging | Action types |

The previews (*Effective access*, *What leaves NimBus*) stay on the same tab as the controls they explain, and they preview the **draft**.

### 8.3 Save and review

The drawer owns one draft for each open feature. Tabs edit the same draft, so switching tabs keeps your edits. The footer counts changed fields. **Review & save** replaces the body with a review screen:

- Each change is listed with a **widens / narrows / changes** chip. Today only MCP classifies changes like this; the other features show *changes*.
- Feature-specific confirmations are kept: MCP's widening checkbox, and Failure intelligence's payload-consent checkbox. **Save** stays disabled until they are ticked.
- The footer states when the change takes effect, as in §2: "Applies within 30 s", "Restart required after saving", and so on.

Under the hood, each feature's existing `save()` is called unchanged. The drawer standardises the frame around the components, not their logic. Heartbeat probing gains a review step it doesn't have today. Because it is just as cheap, Audit gets the same frame.

### 8.4 Unsaved changes

Closing the drawer with a non-empty draft asks "Discard N unsaved changes?". Closing means ✕, Escape or a backdrop click. The question shows *Keep editing* and *Discard*. While edits are unsaved, reloading or closing the browser tab asks the browser's own leave-page question.

The browser Back button is **not** guarded (slice 2 decision, 2026-10-08). Intercepting it needs React Router's `useBlocker`, which only works under a data router, and the app uses a classic `BrowserRouter`. Back closes the drawer and drops the edits. Moving to a data router is a separate decision.

### 8.5 URLs and history

Opening a drawer pushes `/Settings/:feature?tab=:tab`. Switching tabs *replaces* the entry, so Back closes the drawer instead of stepping through tabs. A deep link opens the drawer over the list.

## 9. Changes to other pages

### 9.1 Heartbeat

- A **Platform services** card (today's `PlatformServicesCard`) sits above the fleet table. It is rendered only for site Owners, because `/api/admin/health/services` checks for an Owner. Everyone else sees the page exactly as today.
- A header action **⚙ Configure probing** links to `/Settings/heartbeat`. It is shown only to site Owners.

### 9.2 Simulate

The header gains **⚙ Simulation settings**, linking to `/Settings/simulation`.

### 9.3 Audit Log

The header gains **⚙ Configure recording**, linking to `/Settings/audit`. It is shown only to site Owners.

## 10. Command palette

Add `page`, `operation` and `setting` kinds to `use-palette-search.ts`, built in `components/command-palette/manage-results.ts`. They are client-side and static, and shown only to site Owners:

- **Pages:** the three pages, and the Topology views ("Topology › Catalog drift").
- **Operations:** each operation, e.g. "Bulk resubmit failed", with its group as the subtitle.
- **Settings:** each feature, and each panel tab matched on the tab's keyword index. "rate" finds *MCP access › Limits* and *Simulation › Simulate mode*.

These sections come first, in the order Pages, Operations, Settings, then Endpoints, Event types, Events and Sessions.

Two changes from the original draft (slice 4, 2026-10-08):
- Operations got their own section rather than sharing the `page` kind.
- The "endpoint X — endpoint controls" entries were dropped. `/Operations?endpoint=X` only pre-fills a form and doesn't focus the table row; endpoints are already palette results, and their Operations row is one click away.

## 11. Accessibility and responsiveness

- **Tabs:** the drawer tabs use the existing `components/ui/tabs`. The segmented controls on Topology use `role="tablist"`.
- **Toggles:** every toggle in the Endpoints table has an accessible name, such as "Receive erp-adapter".
- **Drawer:** focus handling comes from `SidePanel`, unchanged.
- **Colour:** status colour is never the only signal. Badges carry text, and attention rows have a text cue as well as the stripe.
- **At 820 px and below:**
  - the drawer is full-screen;
  - the operation list becomes a select;
  - tables scroll horizontally inside their card;
  - each Settings row drops its summary, and its quick action wraps under the title.

## 12. Compatibility

### 12.1 Redirect

`/Admin` (any case) redirects to `/Operations` with `replace`. Old tabs had no URLs, so there is nothing finer to map. Keep the redirect for at least one minor release, and drop it in the next major alongside other `[Obsolete]` removals (`docs/versioning.md`).

### 12.2 Strings and docs that name "Admin → …"

The user-facing ones change in the same PR:

- `Services/Simulation/SimulationService.cs:574, :630`. Reword to "… in Settings → Simulation". No test asserts these texts; only doc comments and a `describe` name mention them (`SimulationApiTests.cs:19`, `simulation-settings.test.tsx:37`), and those are renamed too.
- `ClientApp/src/components/simulate/scenario-bar.tsx:39` and `subscribers-card.tsx:34`.
- `Services/SubscriptionAdminService.cs:598`: "Remove it from Topology → Catalog drift".
- `NimBus.AppHost/Program.cs:178` (startup log line).

The XML doc comments that say "Admin → X API" (`SimulationImplementation.cs:16`, `McpAccessImplementation.cs:17`, …) are reworded where convenient. The `/api/admin/*` routes do **not** change.

24 files under `docs/` mention "Admin → …", 46 times in all. Update the current guides: `features.md`, `heartbeat.md`, `mcp-server.md`, `integration-intelligence.md`, `webapp-simulate.md`, `service-bus-subscription-admin.md`, `storage-providers.md`, `rate-limiting.md`, `asyncapi-mapping.md`, `message-flows.md`, `webapp-rest-api.md`, `sdk-api-reference.md`, `samples/DynamicsBcDemo/README.md` and `talk-track.md`. Leave historical specs, plans and ADRs as they are.

## 13. Open decisions for the repo owner

1. **Where `/Admin` redirects.** `/Operations` (proposed: the incident path) or `/Settings`.
2. **Where Storage lives.** Under Topology (proposed: it is infrastructure) or Settings.
3. **Endpoint Owners on Operations.** An endpoint Owner could get a version of Operations scoped to their own endpoints. That needs server-side authorization by endpoint on the bulk APIs, which are Owner-only today. Out of scope here; worth a follow-up spec.
4. **Collapsing the Manage group.** The sidebar grows by two items for site Owners. Options: accept it (proposed), or make Manage collapsible, remembered per viewer.
5. **The sidebar badge for send-paused endpoints.** Accept the receive-only count (proposed), or add `sendStatus` to `EndpointStatusCount` so one call covers both. That is an API-spec change.

## 14. Implementation slices

Each slice is a separate PR, shippable on its own, with screenshots as AGENTS.md requires.

| Slice | Contents | Risk |
|---|---|---|
| **1. Routes and pages** | `pages/operations.tsx`, `pages/topology.tsx`, `pages/settings.tsx`. Each composes today's components with no visual redesign: Operations = kill switch + the existing accordions; Topology = Subscriptions + audit + export + storage behind a segmented control; Settings = a simple list where each row opens its existing component in `SidePanel`. Add the sidebar items, the route table in `app.tsx`, the `topbar.tsx` breadcrumbs and the `/Admin` redirect, and remove `pages/admin.tsx`. Because the Health tab disappears, Platform services moves to the Heartbeat page (site Owners only, §9.1) in this slice. Failure intelligence drops its phone-only full-screen overlay and `← Back to Admin` link, which would fight the panel. The strings and the docs sweep in §12.2 also land here, so no release ships navigation that its docs no longer describe. | Low. Moves code; logic is unchanged. |
| **2. Settings list and drawer** | Feature registry, row status and summary, the quick-action rule, states, filter, the drawer frame (tabs, sticky footer, review screen, unsaved-changes guard, URL handling), and splitting each settings component's cards into tab sections. | Medium. It touches five settings components; their tests must stay green. |
| **3. Operations redesign** | Status strip, Endpoints table (merging kill switch and counts), master/detail operation list, `initialEndpoint` pre-fill, deep links, the sidebar *paused* badge. | Medium |
| **4. Cross-links and palette** | The ⚙ links on Simulate, Audit Log and Heartbeat, and the palette's `page` and `setting` kinds. | Low |

## 15. Alternatives considered

- **A: keep Admin and give it a grouped sub-navigation** ([alternative-a.html](alternative-a.html)). Admin stays as one page with a left sub-navigation (Operate / Topology / Features), an Overview landing page, and a URL for each section. It is cheaper (mostly `admin.tsx` plus routes) and leaves the sidebar alone. It was rejected because it keeps three different jobs behind one navigation entry and adds a second level of navigation. The repo owner preferred B.
- **Keep the tabs and add an overflow menu.** This hides tabs instead of organising them, and it fixes neither the URL problem nor the invisible feature state.
- **One Settings page that also absorbs Operations and Topology.** "Settings" is the wrong mental model for deleting dead letters. It would bring back the problem in §1.2.

## 16. Testing

- **Unit tests (Vitest):**
  - Replace `pages/admin.test.tsx` with `operations.test.tsx`, `topology.test.tsx` and `settings.test.tsx`.
  - Cover route deep links, including `?endpoint=` pre-fill and `?tab=`.
  - Cover the `/Admin` redirect.
  - Cover the unsaved-changes guard on close, Escape and route change.
  - Cover the quick-action rule: no inline widening.
  - Cover the review screen's required confirmations.
  - Cover the Storage segment rendering only on Cosmos DB.
- **Sidebar tests:** three items for site Owners; none for others, or Access Control only for endpoint Owners. The paused badge treats `null` `subscriptionStatus` as unknown.
- **Existing component tests:** must pass unchanged in slice 1. In slice 2, any change to them must be explained in the PR.
- **Manual checks:** 1440 px and 375 px, light and dark, with screenshots in each PR.

Commands: `npm --prefix src/NimBus.WebApp/ClientApp run test:ci`, `npm --prefix src/NimBus.WebApp/ClientApp run build`, `dotnet build src/NimBus.sln -c Release` (for the C# string changes).
