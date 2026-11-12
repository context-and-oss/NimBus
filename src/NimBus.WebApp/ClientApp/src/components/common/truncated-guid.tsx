import { Tooltip } from "components/ui/tooltip";
import { useToast } from "components/ui/toast";
import { copyToClipboard } from "lib/clipboard";
import { cn } from "lib/utils";
import { Link } from "react-router-dom";

interface ITruncatedGuidProps {
  guid: string | undefined | null;
  displayLength?: number;
  /** When false, the trailing copy affordance won't render. Default: true. */
  withCopy?: boolean;
  /**
   * If provided, clicking the truncated value invokes this callback with the
   * full GUID instead of copying to the clipboard. The copy icon always
   * copies regardless. When omitted, the value itself remains click-to-copy
   * (the historical behavior).
   */
  onClick?: (guid: string) => void;
  className?: string;
  /** Makes the value a details link; the copy button remains independent. */
  linkTo?: string;
}

/**
 * Renders a truncated machine ID as monospace + tabular-nums (design rec §07).
 * The copy icon fades in on row hover via the parent's `.group` class so
 * resting tables stay quiet. The value text and the copy icon are independent
 * click targets: the value invokes `onClick` if provided (falling back to
 * copy), the icon always copies.
 */
export default function TruncatedGuid({
  guid,
  displayLength = 8,
  withCopy = true,
  onClick,
  className,
  linkTo,
}: ITruncatedGuidProps) {
  const { addToast } = useToast();

  if (!guid) {
    return <span className="text-muted-foreground">—</span>;
  }

  const truncated =
    guid.length > displayLength ? `${guid.substring(0, displayLength)}…` : guid;

  const copy = async () => {
    try {
      await copyToClipboard(guid);
      addToast({
        title: "Copied to clipboard",
        description: guid,
        variant: "success",
        duration: 2000,
      });
    } catch {
      addToast({
        title: "Failed to copy",
        description: "Clipboard access not available",
        variant: "error",
        duration: 2000,
      });
    }
  };

  const handleValueClick = (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (onClick) {
      onClick(guid);
    } else {
      void copy();
    }
  };

  const handleCopyClick = (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    void copy();
  };

  return (
    <Tooltip content={guid} position="top">
      <span className={cn("inline-flex items-center gap-1", className)}>
        {linkTo ? (
          <Link
            to={linkTo}
            onClick={(e) => {
              e.stopPropagation();
              if (
                onClick &&
                !e.metaKey &&
                !e.ctrlKey &&
                !e.shiftKey &&
                !e.altKey &&
                e.button === 0
              ) {
                e.preventDefault();
                onClick(guid);
              }
            }}
            className="font-mono text-[12px] tabular-nums text-foreground hover:text-primary hover:underline"
          >
            {truncated}
          </Link>
        ) : (
          <button
            type="button"
            onClick={handleValueClick}
            className="bg-transparent border-0 p-0 m-0 cursor-pointer font-mono text-[12px] tabular-nums text-foreground hover:text-primary"
          >
            {truncated}
          </button>
        )}
        {withCopy && (
          <button
            type="button"
            onClick={handleCopyClick}
            aria-label="Copy to clipboard"
            className="bg-transparent border-0 p-0 m-0 cursor-pointer text-foreground opacity-0 group-hover:opacity-60 hover:opacity-100! transition-opacity shrink-0"
          >
            <svg
              aria-hidden="true"
              width="11"
              height="11"
              viewBox="0 0 16 16"
              fill="none"
            >
              <rect
                x="5"
                y="5"
                width="9"
                height="9"
                rx="1.5"
                stroke="currentColor"
                strokeWidth="1.4"
              />
              <path
                d="M3 11V3.5C3 2.7 3.7 2 4.5 2H11"
                stroke="currentColor"
                strokeWidth="1.4"
                strokeLinecap="round"
              />
            </svg>
          </button>
        )}
      </span>
    </Tooltip>
  );
}
