import { Link } from "react-router-dom";
import { cn } from "lib/utils";

export interface TopologyMiniMapProps {
  /** Producer endpoint names; rendered as a vertical stack on the left. */
  producers: string[];
  /** Consumer endpoint names; rendered as a vertical stack on the right. */
  consumers: string[];
  /** Spec 045: a routing label per consumer name ("keys 01, 02", "unmatched"). */
  consumerNotes?: Record<string, string>;
  /** Center node label (e.g. the event type name). */
  centerLabel: string;
  /** Center node sub-meta (e.g. "v1.0 · 3 fields"). */
  centerMeta?: string;
  /** Producer endpoint route builder. */
  endpointRoute?: (name: string) => string;
  className?: string;
}

/**
 * Producer → Event Type → Consumer topology mini-map (design rec §09 / §08).
 *
 * NimBus is fundamentally a graph product, so we render the *edge* explicitly
 * rather than two unconnected lists. Each side stays clickable so the operator
 * can jump straight into a related endpoint without re-navigating.
 *
 * Colour legend (preserved from previous two-panel layout): producer = success,
 * consumer = info, center = primary tint.
 */
export const TopologyMiniMap: React.FC<TopologyMiniMapProps> = ({
  producers,
  consumers,
  consumerNotes,
  centerLabel,
  centerMeta,
  endpointRoute = (name) => `/Endpoints/Details/${name}`,
  className,
}) => {
  return (
    <div
      className={cn(
        "bg-card border border-border rounded-nb-md p-5",
        "flex items-stretch gap-4",
        className,
      )}
    >
      <Stack
        role="Producer"
        names={producers}
        emptyLabel="No producers"
        accent="success"
        endpointRoute={endpointRoute}
      />

      <Edge direction="right" tone="from-success" />

      <div
        className={cn(
          "self-center text-center px-4 py-3 rounded-nb-md",
          "bg-primary-tint border border-primary",
          "min-w-[180px]",
        )}
      >
        <div className="font-mono text-[10px] tracking-[0.14em] uppercase text-muted-foreground mb-1">
          Event Type
        </div>
        <div className="font-bold text-[14px] text-foreground wrap-break-word">
          {centerLabel}
        </div>
        {centerMeta && (
          <div className="font-mono text-[10px] text-muted-foreground mt-1">
            {centerMeta}
          </div>
        )}
      </div>

      <Edge direction="right" tone="to-info" />

      <Stack
        role="Consumer"
        names={consumers}
        notes={consumerNotes}
        emptyLabel="No consumers"
        accent="info"
        endpointRoute={endpointRoute}
      />
    </div>
  );
};

interface StackProps {
  role: "Producer" | "Consumer";
  names: string[];
  notes?: Record<string, string>;
  emptyLabel: string;
  accent: "success" | "info";
  endpointRoute: (name: string) => string;
}

const Stack: React.FC<StackProps> = ({
  role,
  names,
  notes,
  emptyLabel,
  accent,
  endpointRoute,
}) => {
  const accentClasses =
    accent === "success"
      ? "border-status-success bg-status-success-50 dark:bg-green-950/40"
      : "border-status-info bg-status-info-50 dark:bg-blue-950/40";
  const linkColor =
    accent === "success"
      ? "text-status-success"
      : "text-status-info";

  return (
    <div className="flex flex-col gap-2 min-w-[180px] flex-1">
      {names.length === 0 ? (
        <div
          className={cn(
            "text-center px-3 py-3 rounded-nb-md border border-dashed border-border",
            "text-[12px] text-muted-foreground italic",
          )}
        >
          {emptyLabel}
        </div>
      ) : (
        names.map((name) => (
          <Link
            key={name}
            to={endpointRoute(name)}
            className={cn(
              "block px-3 py-2 rounded-nb-md border-1.5 border no-underline",
              "text-center transition-colors hover:shadow-nb-sm",
              accentClasses,
            )}
          >
            <div className="font-mono text-[10px] tracking-[0.14em] uppercase text-muted-foreground">
              {role} · {names.length}
            </div>
            <div className={cn("font-bold text-[14px]", linkColor)}>
              {name}
            </div>
            {notes?.[name] && (
              <div className="font-mono text-[11px] text-muted-foreground mt-0.5">
                {notes[name]}
              </div>
            )}
          </Link>
        ))
      )}
    </div>
  );
};

interface EdgeProps {
  direction: "right";
  tone: "from-success" | "to-info";
}

const Edge: React.FC<EdgeProps> = ({ tone }) => {
  // Gradient picks up success→primary on the left edge and primary→info on the
  // right, so colour direction reinforces semantic flow (publisher → consumer).
  const gradient =
    tone === "from-success"
      ? "from-status-success to-primary"
      : "from-primary to-status-info";

  return (
    <div className="self-center flex-1 min-w-[32px] relative">
      <div
        className={cn(
          "h-0.5 w-full bg-linear-to-r rounded-full",
          gradient,
        )}
      />
      <span
        className={cn(
          "absolute top-[-7px] right-[-2px] text-[10px]",
          tone === "from-success" ? "text-primary" : "text-status-info",
        )}
        aria-hidden="true"
      >
        ▶
      </span>
    </div>
  );
};
