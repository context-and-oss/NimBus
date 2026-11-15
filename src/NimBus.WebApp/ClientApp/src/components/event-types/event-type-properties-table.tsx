import { Tooltip } from "components/ui/tooltip";
import { cn } from "lib/utils";
import * as api from "api-client";

interface IEventTypePropertiesTableProps {
  properties: api.EventTypeProperty[];
  /** The property named by the class's [SessionKey]: its value is the default session key. */
  sessionKeyProperty?: string;
  /** The property named by the class's [RoutingKey] (Spec 045). */
  routingKeyProperty?: string;
}

/**
 * Schema table for an event type. Design rec §09 schema: Required ≠ failed —
 * drop the red badge that used to mark required fields and use neutral ink
 * with a leading dot instead. Red is reserved for actual failure states.
 */
const EventTypePropertiesTable: React.FC<IEventTypePropertiesTableProps> = ({
  properties,
  sessionKeyProperty,
  routingKeyProperty,
}) => {
  // Filter out internal MessageMetadata property
  const displayProperties = properties.filter(
    (p) => p.name !== "MessageMetadata",
  );

  return (
    <div className="bg-card border border-border rounded-nb-md overflow-hidden">
      <div className="px-4 py-3 border-b border-border">
        <h3 className="m-0 font-bold text-[13px]">Properties</h3>
      </div>
      {displayProperties.length === 0 ? (
        <p className="px-4 py-6 text-muted-foreground text-sm">
          No properties defined
        </p>
      ) : (
        <table className="w-full text-[13px]">
          <thead className="bg-muted">
            <tr>
              <Th>Name</Th>
              <Th>Type</Th>
              <Th>Required</Th>
              <Th className="w-[40%]">Description</Th>
            </tr>
          </thead>
          <tbody>
            {displayProperties.map((prop, index) => (
              <tr
                key={prop.name ?? `prop-${index}`}
                className="border-t border-border align-top"
              >
                <td className="px-3.5 py-2.5">
                  <div className="flex flex-wrap items-center gap-1.5">
                    <span className="font-bold">{prop.name || "—"}</span>
                    {prop.name && prop.name === sessionKeyProperty && (
                      <KeyTag tooltip="The default session key: events with the same value are processed in order, one at a time.">
                        Session key
                      </KeyTag>
                    )}
                    {prop.name && prop.name === routingKeyProperty && (
                      <KeyTag tooltip="The routing key: consumers that declare keys receive only events whose value they claim.">
                        Routing key
                      </KeyTag>
                    )}
                  </div>
                </td>
                <td className="px-3.5 py-2.5">
                  {prop.typeFullName && prop.typeFullName !== prop.typeName ? (
                    <Tooltip content={prop.typeFullName} position="top">
                      <TypeTag>{prop.typeName}</TypeTag>
                    </Tooltip>
                  ) : (
                    <TypeTag>{prop.typeName}</TypeTag>
                  )}
                </td>
                <td className="px-3.5 py-2.5">
                  <RequiredCell required={!!prop.isRequired} />
                </td>
                <td className="px-3.5 py-2.5 text-muted-foreground">
                  {prop.description || "—"}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
};

const Th: React.FC<{ children: React.ReactNode; className?: string }> = ({
  children,
  className,
}) => (
  <th
    className={cn(
      "text-left text-[11px] font-semibold uppercase tracking-[0.06em]",
      "text-muted-foreground px-3.5 py-2.5 whitespace-nowrap",
      className,
    )}
  >
    {children}
  </th>
);

const TypeTag: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <span
    className={cn(
      "font-mono text-[11.5px] px-2 py-0.5 rounded-nb-sm cursor-help",
      "bg-nimbus-purple-50 text-nimbus-purple",
      "dark:bg-purple-950/40 dark:text-purple-300",
    )}
  >
    {children}
  </span>
);

const KeyTag: React.FC<{ children: React.ReactNode; tooltip: string }> = ({
  children,
  tooltip,
}) => (
  <Tooltip content={tooltip} position="top">
    <span
      className={cn(
        "font-mono text-[10.5px] uppercase tracking-[0.06em] px-1.5 py-0.5",
        "rounded-nb-sm border border-border text-muted-foreground cursor-help",
      )}
    >
      {children}
    </span>
  </Tooltip>
);

const RequiredCell: React.FC<{ required: boolean }> = ({ required }) => {
  if (required) {
    return (
      <span className="inline-flex items-center gap-1.5 text-foreground text-[12.5px] font-semibold">
        <span
          aria-hidden="true"
          className="inline-block w-1.5 h-1.5 rounded-full bg-foreground"
        />
        Required
      </span>
    );
  }
  return (
    <span className="inline-flex items-center gap-1.5 text-muted-foreground text-[12.5px] font-medium">
      <span
        aria-hidden="true"
        className="inline-block w-1.5 h-1.5 rounded-full bg-muted-foreground/50"
      />
      Optional
    </span>
  );
};

export default EventTypePropertiesTable;
