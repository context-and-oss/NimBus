import {
  createContext,
  useContext,
  useState,
  type ReactNode,
  type HTMLAttributes,
} from "react";
import { cn } from "lib/utils";

// Context for tab state
interface TabsContextValue {
  activeIndex: number;
  setActiveIndex: (index: number) => void;
}

const TabsContext = createContext<TabsContextValue | null>(null);

function useTabsContext() {
  const context = useContext(TabsContext);
  if (!context) {
    throw new Error("Tabs components must be used within a Tabs provider");
  }
  return context;
}

// Tabs container
export interface TabsProps extends HTMLAttributes<HTMLDivElement> {
  defaultIndex?: number;
  /** Controlled mode — when set, the active tab follows this prop and
      `onTabChange` reports clicks; internal state is bypassed. */
  index?: number;
  onTabChange?: (index: number) => void;
  isLazy?: boolean;
  isFitted?: boolean;
  variant?: "enclosed" | "line";
  children: ReactNode;
}

const Tabs = ({
  defaultIndex = 0,
  index,
  onTabChange,
  isLazy = true,
  isFitted = false,
  variant = "enclosed",
  className,
  children,
  ...props
}: TabsProps) => {
  const [internalIndex, setInternalIndex] = useState(defaultIndex);
  const activeIndex = index ?? internalIndex;
  const setActiveIndex = (i: number) => {
    if (index === undefined) setInternalIndex(i);
    onTabChange?.(i);
  };

  return (
    <TabsContext.Provider value={{ activeIndex, setActiveIndex }}>
      <div
        className={cn("flex flex-col w-full", className)}
        data-variant={variant}
        data-fitted={isFitted}
        data-lazy={isLazy}
        {...props}
      >
        {children}
      </div>
    </TabsContext.Provider>
  );
};

// Tab list container
export interface TabListProps extends HTMLAttributes<HTMLDivElement> {
  children: ReactNode;
}

const TabList = ({ className, children, ...props }: TabListProps) => {
  return (
    <div
      role="tablist"
      className={cn("flex border-b border-border", className)}
      {...props}
    >
      {children}
    </div>
  );
};

// Individual tab button
export interface TabProps extends HTMLAttributes<HTMLButtonElement> {
  index: number;
  isDisabled?: boolean;
  children: ReactNode;
}

const Tab = ({
  index,
  isDisabled = false,
  className,
  children,
  ...props
}: TabProps) => {
  const { activeIndex, setActiveIndex } = useTabsContext();
  const isActive = activeIndex === index;

  return (
    <button
      role="tab"
      type="button"
      aria-selected={isActive}
      aria-disabled={isDisabled}
      tabIndex={isActive ? 0 : -1}
      disabled={isDisabled}
      className={cn(
        "flex-1 px-4 py-2 text-sm font-semibold transition-colors",
        "border border-b-0 rounded-t-md -mb-px",
        "focus:outline-hidden focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-inset",
        isActive
          ? "bg-primary/10 text-primary border-primary dark:bg-primary/25 dark:text-primary-200"
          : "bg-background text-muted-foreground border-border hover:bg-accent hover:text-foreground",
        isDisabled && "opacity-50 cursor-not-allowed hover:bg-background",
        className,
      )}
      onClick={() => !isDisabled && setActiveIndex(index)}
      {...props}
    >
      {children}
    </button>
  );
};

// Tab panels container
export interface TabPanelsProps extends HTMLAttributes<HTMLDivElement> {
  children: ReactNode;
}

const TabPanels = ({ className, children, ...props }: TabPanelsProps) => {
  return (
    <div className={cn("flex flex-1 min-h-0", className)} {...props}>
      {children}
    </div>
  );
};

// Individual tab panel
export interface TabPanelProps extends HTMLAttributes<HTMLDivElement> {
  index: number;
  isLazy?: boolean;
  children: ReactNode;
}

const TabPanel = ({
  index,
  isLazy = true,
  className,
  children,
  ...props
}: TabPanelProps) => {
  const { activeIndex } = useTabsContext();
  const isActive = activeIndex === index;

  // If lazy rendering is enabled and this panel is not active, don't render content
  if (isLazy && !isActive) {
    return null;
  }

  // If not lazy, hide inactive panels with CSS. The display class must go —
  // an explicit `display: flex` overrides the `hidden` attribute's
  // UA `display: none`, so the inline style makes the hiding unambiguous.
  if (!isActive) {
    return (
      <div
        role="tabpanel"
        hidden
        style={{ display: "none" }}
        className={cn("flex overflow-auto w-full", className)}
        {...props}
      >
        {children}
      </div>
    );
  }

  return (
    <div
      role="tabpanel"
      className={cn("flex overflow-auto w-full", className)}
      {...props}
    >
      {children}
    </div>
  );
};

export { Tabs, TabList, Tab, TabPanels, TabPanel };
