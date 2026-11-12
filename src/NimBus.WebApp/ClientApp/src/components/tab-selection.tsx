import * as React from "react";
import { useLocation } from "react-router-dom";
import { Tabs, TabList, Tab, TabPanels, TabPanel } from "components/ui/tabs";
import { cn } from "lib/utils";

export interface ITab {
  name: string;
  content: React.JSX.Element;
  isEnabled: boolean;
  /** Optional count shown as a pill next to the name. */
  count?: number | string;
  /** Pill tone for `count`; "warning" flags work waiting on the operator. */
  countTone?: "default" | "warning";
}

interface ITabSelectionsProps {
  tabs: ITab[];
  /** "enclosed" (default): fitted boxed tabs in a bordered panel. "line": underline tabs, no panel. */
  variant?: "enclosed" | "line";
  /** Controlled active tab; omit to let the tabs manage it. */
  index?: number;
  onTabChange?: (index: number) => void;
}

const TabSelection: React.FunctionComponent<ITabSelectionsProps> = (props) => {
  const locationState = useLocation().state as { tabIndex: string };
  const index = locationState ? parseInt(locationState.tabIndex) : 0;

  if (!props?.tabs) {
    return null;
  }

  const line = props.variant === "line";

  return (
    <Tabs
      isLazy={true}
      isFitted={!line}
      variant={props.variant ?? "enclosed"}
      defaultIndex={isNaN(index) ? 0 : index}
      index={props.index}
      onTabChange={props.onTabChange}
      className={line ? "gap-5" : "p-4 border border-input rounded-sm"}
    >
      <TabList className={line ? "flex-wrap gap-x-6 gap-y-1" : "mb-4"}>
        {props.tabs.map((tab, idx) => (
          <Tab key={`tab-${tab.name}`} index={idx} isDisabled={!tab.isEnabled}>
            {tab.name}
            {tab.count !== undefined && (
              <span
                className={cn(
                  "font-mono text-[11px] font-medium px-1.5 py-px rounded-full",
                  tab.countTone === "warning"
                    ? "bg-status-warning-50 text-status-warning-ink dark:bg-yellow-950/40 dark:text-yellow-200"
                    : "bg-muted text-muted-foreground",
                )}
              >
                {tab.count}
              </span>
            )}
          </Tab>
        ))}
      </TabList>
      <TabPanels>
        {props.tabs.map((tab, idx) => (
          <TabPanel key={`tabpanel-${tab.name}`} index={idx}>
            {tab.content}
          </TabPanel>
        ))}
      </TabPanels>
    </Tabs>
  );
};

export default TabSelection;
