import {
  availableTopologyViews,
  OPERATION_GROUPS,
  OPERATIONS,
  SETTINGS_FEATURES,
} from "models/manage-pages";
import type { PaletteResult } from "./use-palette-search";

// Command-palette entries for the Manage pages (Spec 038 §10): the pages and
// Topology views, each bulk operation, and each Settings feature and tab. Built
// from the static registries, so nothing is fetched. Site Owners only; the
// caller decides.

const MAX_PER_SECTION = 8;

interface Candidate extends PaletteResult {
  /** Lower-cased text the query is matched against. */
  match: string;
}

// The Topology views depend on the storage provider, the same as on the Topology page.
const pages = (storageProvider: string | undefined): Candidate[] =>
  [
    { key: "page::operations", kind: "page" as const, title: "Operations", subtitle: "Endpoints and bulk operations", route: "/Operations" },
    { key: "page::topology", kind: "page" as const, title: "Topology", subtitle: "Service Bus and storage", route: "/Topology" },
    { key: "page::settings", kind: "page" as const, title: "Settings", subtitle: "Opt-in capabilities", route: "/Settings" },
    ...availableTopologyViews(storageProvider).map((v) => ({
      key: `page::topology::${v.id}`,
      kind: "page" as const,
      title: `Topology › ${v.label}`,
      subtitle: "Topology",
      route: `/Topology/${v.id}`,
    })),
  ].map((p) => ({ ...p, match: `${p.title} ${p.subtitle}`.toLowerCase() }));

const OPERATION_ENTRIES: Candidate[] = OPERATIONS.map((o) => ({
  key: `operation::${o.id}`,
  kind: "operation" as const,
  title: o.label,
  subtitle: OPERATION_GROUPS.find((g) => g.id === o.group)?.label,
  route: `/Operations/${o.id}`,
  match: `${o.label} ${o.description}`.toLowerCase(),
}));

// A feature matches on its name and description; a tab on its own label and
// keywords, so "rate" finds the tabs holding rate limits without listing every
// tab of a feature whose name happens to match.
const SETTING_ENTRIES: Candidate[] = SETTINGS_FEATURES.flatMap((f) => [
  {
    key: `setting::${f.id}`,
    kind: "setting" as const,
    title: f.name,
    subtitle: f.group,
    route: `/Settings/${f.id}`,
    match: `${f.name} ${f.description}`.toLowerCase(),
  },
  ...f.tabs.map((t) => ({
    key: `setting::${f.id}::${t.id}`,
    kind: "setting" as const,
    title: `${f.name} › ${t.label}`,
    subtitle: f.group,
    route: `/Settings/${f.id}?tab=${t.id}`,
    match: `${t.label} ${t.keywords}`.toLowerCase(),
  })),
]);

const pick = (candidates: Candidate[], query: string): PaletteResult[] =>
  candidates
    .filter((c) => c.match.includes(query))
    .slice(0, MAX_PER_SECTION)
    .map(({ match: _match, ...result }) => result);

/** Matching Manage entries, pages first, then operations, then settings. */
export function manageResults(query: string, storageProvider?: string): PaletteResult[] {
  const q = query.trim().toLowerCase();
  if (!q) return [];
  return [...pick(pages(storageProvider), q), ...pick(OPERATION_ENTRIES, q), ...pick(SETTING_ENTRIES, q)];
}
