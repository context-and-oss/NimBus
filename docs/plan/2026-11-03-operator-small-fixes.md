# Operator small fixes

## Scope

One worktree and branch from master. Fix the reported small and small–medium
operator issues without changing metrics aggregation, measurement periods,
authorization policy, transport behavior, or generated API files by hand.

## Implementation

1. Add failing regression tests for local date/time consistency, failure-only
   error grouping, event navigation and Failed breadcrumbs, resubmit editor
   behavior/formatting, empty exception content, and alert setup guidance.
2. Use shared local date/time formatting with a visible time-zone label. Filter
   grouped errors to failure states while retaining failures without error text.
3. Reuse the resubmit editor from the Failed drawer, preserving payload masking,
   permissions, asynchronous payload loading, and accepted-command feedback.
   Pretty-print editable JSON in a monospace textarea.
4. Provide explicit event-detail links while retaining useful filter actions;
   preserve Failed navigation context, and hide empty exception headings.
5. Explain unavailable Alerts and where subscriptions can be configured. Correct
   PII Reader copy to describe masking of fields marked Sensitive.
6. Reproduce the Moment warning and fix the source API schema/serialization or
   caller. Regenerate contracts through the normal WebApp build if necessary.
7. Add failing backend logging tests. Downgrade routine authorization bypass
   diagnostics and expected session deferrals; ensure one authoritative Error
   log per handler failure without losing unexpected pipeline failure logs.

## Verification

- Run focused tests RED, then GREEN for changed behavior (Vitest and MSTest).
- Run the terminating frontend suite, TypeScript/Vite build, and relevant .NET
  tests. Run full solution Release build/test before pushing.
- Inspect diff and perform a code review; do not invoke cross-model review-change.
- Verify finished UI in an isolated worktree instance, capture a representative
  screenshot for the PR, and retain the user's existing Aspire stack.
- Report any integration tests skipped for missing provider credentials.

## Excluded

Conflicting Metrics totals and Flow's mixed rate/count measurement periods need
a separate correctness investigation and are explicitly outside this branch.

## Findings and implementation

- Error groups now include only Failed, DeadLettered and Unsupported messages;
  failed rows without stored error text remain represented.
- Grouped views, Insights, Failed rows and the event Flow use local timestamps.
  The app header names the local time zone, and Flow labels its timestamps.
- The shared resubmit editor opens inside the Failed drawer, uses a monospace
  multiline JSON draft, retains edits after rejected commands, handles late
  history without overwriting an edited draft, and disables accepted attempts.
- Messages has direct event links plus a separate Filter action. Failed IDs
  open the drawer and full-page URLs retain Failed breadcrumb context.
- Alerts explains missing subscriptions and links to the endpoint list. Empty
  exception headings are hidden. PII Reader wording describes Sensitive fields.
- The Moment warning came from reparsing hydrated DTOs. Converting those DTOs
  to ISO JSON first preserves server-rendered raw JSON support as well.
- Strict handling owns handler-failure Error logs. Logging middleware preserves
  unexpected errors; expected session deferral and configured authorization
  bypasses no longer generate Warning/Error noise.

## Verification progress

- Confirmed failing UI regressions before implementation, including the global
  Messages link, late history and server-rendered timestamp input paths.
- Confirmed failing backend regressions for duplicate Error entries, deferral
  severity/stack traces, and the authorization-bypass warning.
- Frontend suite: 834 tests passed. Additional final focused run: 15 passed.
- Frontend production build passed; WebApp Release build (NSwag enabled) passed.
- Changed TypeScript files pass ESLint with one existing warning in event-details
  about its pre-existing route-generation ref cleanup.
- Solution Release build passed (two existing ASPIRE010 warnings). Full solution
  Release tests passed: 3,515 passed, zero failed, 437 skipped because live SQL,
  Cosmos or Service Bus test connections were not configured.
- Diff whitespace checks passed. Review retained the original payload-selection
  rules, guarded late drawer command responses after navigation, and verified
  that unexpected pipeline and invalid handoff errors remain Error logs.
- Browser verification and the PR screenshot are unavailable in this session:
  the browser access host is unavailable, and native app discovery timed out.
  No isolated Aspire instance was started without a usable UI verification path;
  the user's original Aspire stack was retained.
