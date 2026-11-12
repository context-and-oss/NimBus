import {
  useState,
  useRef,
  useEffect,
  useLayoutEffect,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";
import { cn } from "lib/utils";
import { Checkbox } from "./checkbox";

// The open list's tallest height (eight options), and the room below the field under
// which it opens upward instead, if there is more room above.
const LIST_MAX_HEIGHT = 288;
const LIST_MIN_HEIGHT = 160;
const LIST_GAP = 4;
const VIEWPORT_MARGIN = 8;

interface ListPlacement {
  top?: number;
  bottom?: number;
  left: number;
  width: number;
  maxHeight: number;
}

export interface ComboboxOption {
  value: string;
  label: string;
}

export interface ComboboxProps {
  options: ComboboxOption[];
  value?: string[];
  onChange?: (value: string[]) => void;
  /**
   * Called once a change is settled: when the list closes with a different selection than
   * it opened with, or when a chip is removed while the list is closed. Lets a filter apply
   * a multi-pick in one go instead of once per click.
   */
  onCommit?: (value: string[]) => void;
  placeholder?: string;
  label?: string;
  multiple?: boolean;
  disabled?: boolean;
  className?: string;
}

const Combobox = ({
  options,
  value = [],
  onChange,
  onCommit,
  placeholder = "Select...",
  label,
  multiple = true,
  disabled = false,
  className,
}: ComboboxProps) => {
  const [isOpen, setIsOpen] = useState(false);
  const [search, setSearch] = useState("");
  const containerRef = useRef<HTMLDivElement>(null);
  const fieldRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const [placement, setPlacement] = useState<ListPlacement | null>(null);
  const valueKey = JSON.stringify(value);
  const valueAtOpen = useRef(valueKey);
  const wasOpen = useRef(false);

  useEffect(() => {
    if (isOpen && !wasOpen.current) valueAtOpen.current = valueKey;
    if (!isOpen && wasOpen.current && valueKey !== valueAtOpen.current)
      onCommit?.(value);
    wasOpen.current = isOpen;
    // Only the open/close transition matters; value is read as of that moment.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen]);

  // Close on outside click. The list is in a portal, outside the container.
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node;
      if (
        containerRef.current &&
        !containerRef.current.contains(target) &&
        !listRef.current?.contains(target)
      ) {
        setIsOpen(false);
      }
    };
    document.addEventListener("mousedown", handleClickOutside);
    return () => document.removeEventListener("mousedown", handleClickOutside);
  }, []);

  // The open list is a popup over the page: fixed to the field, in a portal, so no
  // container's overflow clips it and it never makes the page longer. It opens below
  // the field, or above it when there is more room there, and takes at most the room
  // left in the window, scrolling inside when the options need more. It follows the
  // field as the page scrolls or the window resizes.
  useLayoutEffect(() => {
    if (!isOpen) {
      setPlacement(null);
      return;
    }
    const place = () => {
      const field = fieldRef.current;
      if (!field) return;
      const r = field.getBoundingClientRect();
      const below = window.innerHeight - r.bottom - LIST_GAP - VIEWPORT_MARGIN;
      const above = r.top - LIST_GAP - VIEWPORT_MARGIN;
      const openUp = below < LIST_MIN_HEIGHT && above > below;
      setPlacement({
        left: r.left,
        width: r.width,
        maxHeight: Math.max(
          0,
          Math.min(LIST_MAX_HEIGHT, openUp ? above : below),
        ),
        ...(openUp
          ? { bottom: window.innerHeight - r.top + LIST_GAP }
          : { top: r.bottom + LIST_GAP }),
      });
    };
    place();
    window.addEventListener("scroll", place, true);
    window.addEventListener("resize", place);
    return () => {
      window.removeEventListener("scroll", place, true);
      window.removeEventListener("resize", place);
    };
  }, [isOpen]);

  const filteredOptions = options.filter((option) =>
    option.label.toLowerCase().includes(search.toLowerCase()),
  );

  const handleSelect = (optionValue: string) => {
    if (!onChange) return;

    if (multiple) {
      const newValue = value.includes(optionValue)
        ? value.filter((v) => v !== optionValue)
        : [...value, optionValue];
      onChange(newValue);
      // A chip removed with the list closed has no close to wait for.
      if (!isOpen) onCommit?.(newValue);
    } else {
      onChange([optionValue]);
      setIsOpen(false);
      setSearch("");
    }
  };

  const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    setSearch(e.target.value);
    if (!isOpen) setIsOpen(true);
  };

  const handleInputClick = () => {
    if (!multiple && value.length > 0 && !isOpen) {
      setSearch("");
    }
    setIsOpen(true);
  };

  const selectedLabels = value
    .map((v) => options.find((o) => o.value === v)?.label)
    .filter(Boolean);

  return (
    <div ref={containerRef} className={cn("relative w-full", className)}>
      {label && (
        <label className="block text-sm font-medium text-foreground mb-1">
          {label}
        </label>
      )}
      <div
        ref={fieldRef}
        className={cn(
          // min-h-10 matches Input, so a combobox lines up with the inputs beside it.
          // gap-1 keeps the last chip's x clear of the arrow.
          "flex min-h-10 items-center gap-1 border border-input rounded-md bg-background",
          "focus-within:ring-2 focus-within:ring-primary focus-within:border-primary",
          disabled && "bg-muted cursor-not-allowed",
        )}
        // The whole box opens the list, so a search input squeezed to nothing beside the
        // chips is no obstacle.
        onClick={() => {
          if (disabled) return;
          setIsOpen(true);
          inputRef.current?.focus();
        }}
      >
        <div className="flex flex-wrap gap-1 p-1 flex-1 min-w-0">
          {multiple && value.length > 0 && (
            // contents: chips and the search input wrap as one line of items, so the input
            // sits after the last chip instead of taking a line of its own.
            <div className="contents">
              {selectedLabels.map((label, idx) => (
                <span
                  key={idx}
                  title={label}
                  className="inline-flex max-w-full min-w-0 items-center gap-1 whitespace-nowrap px-2 py-0.5 bg-primary-100 text-primary-800 dark:bg-primary-900/40 dark:text-primary-200 text-xs rounded-sm"
                >
                  {/* A long name is cut short rather than widening the field. */}
                  <span className="truncate">{label}</span>
                  <button
                    type="button"
                    onClick={(e) => {
                      e.stopPropagation();
                      handleSelect(value[idx]);
                    }}
                    aria-label={`Remove ${label}`}
                    className="shrink-0 hover:text-primary-600 dark:hover:text-primary-300"
                  >
                    <svg
                      className="w-3 h-3"
                      fill="currentColor"
                      viewBox="0 0 20 20"
                    >
                      <path
                        fillRule="evenodd"
                        d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
                        clipRule="evenodd"
                      />
                    </svg>
                  </button>
                </span>
              ))}
            </div>
          )}
          <input
            ref={inputRef}
            type="text"
            className={cn(
              "flex-1 outline-hidden text-sm py-1 px-1 bg-transparent text-foreground placeholder:text-muted-foreground",
              // With chips there is no placeholder to show. Closed, the input leaves the flow
              // (still focusable) so it never adds a line under the chips; open, it gets room
              // to type in.
              multiple && value.length > 0
                ? isOpen
                  ? "min-w-16"
                  : "absolute h-px w-px min-w-0 opacity-0"
                : "min-w-[100px]",
            )}
            placeholder={
              value.length === 0
                ? placeholder
                : !multiple
                  ? (selectedLabels[0] ?? "")
                  : ""
            }
            value={search}
            onChange={handleInputChange}
            onClick={handleInputClick}
            onKeyDown={(e) => {
              if (e.key === "Escape") setIsOpen(false);
            }}
            disabled={disabled}
          />
        </div>
        <button
          type="button"
          className="px-2 py-2 text-muted-foreground hover:text-foreground"
          onClick={(e) => {
            e.stopPropagation();
            if (!disabled) setIsOpen(!isOpen);
          }}
          disabled={disabled}
        >
          <svg
            className={cn(
              "w-4 h-4 transition-transform",
              isOpen && "rotate-180",
            )}
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
        </button>
      </div>

      {isOpen &&
        !disabled &&
        placement &&
        createPortal(
          <div
            ref={listRef}
            style={{ position: "fixed", ...placement }}
            className="z-50 bg-popover text-popover-foreground border border-border rounded-md shadow-lg overflow-auto overscroll-contain"
          >
            {filteredOptions.length === 0 ? (
              <div className="px-3 py-2 text-sm text-muted-foreground">
                No options found
              </div>
            ) : (
              filteredOptions.map((option) => {
                const isSelected = value.includes(option.value);
                return (
                  <div
                    key={option.value}
                    className={cn(
                      "flex items-center gap-2 px-3 py-2 cursor-pointer hover:bg-accent",
                      isSelected && "bg-primary-50 dark:bg-primary-900/30",
                    )}
                    onClick={() => handleSelect(option.value)}
                  >
                    {multiple && (
                      <Checkbox
                        checked={isSelected}
                        onChange={() => {}}
                        aria-label={option.label}
                      />
                    )}
                    <span className="text-sm break-all">{option.label}</span>
                  </div>
                );
              })
            )}
          </div>,
          document.body,
        )}
    </div>
  );
};

export { Combobox };
