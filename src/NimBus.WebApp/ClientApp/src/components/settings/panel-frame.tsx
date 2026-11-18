import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";
import { InspectorPortal, Workspace, WorkspaceTabs } from "components/workspace/workspace";

// The Settings frame (Spec 038 §8): the workspace around one feature's
// settings. A settings component opts into the frame through the helpers
// below; rendered standalone (no frame above it) every helper falls back to
// rendering in place, so the component works and tests the same either way.

interface PanelFrameValue {
  activeTab: string;
  reviewing: boolean;
  footer: HTMLElement | null;
  setDirty: (count: number) => void;
  setReviewing: (reviewing: boolean) => void;
}

const PanelFrameContext = createContext<PanelFrameValue | null>(null);

/** True when rendered inside the Settings panel frame. */
export const useInPanel = (): boolean => useContext(PanelFrameContext) !== null;

/**
 * One tab's worth of a settings component. Inside the frame it renders only on
 * its tab, and not while the review screen is showing; standalone it always renders.
 */
export function PanelSection({ tab, children }: { tab: string; children: ReactNode }) {
  const frame = useContext(PanelFrameContext);
  if (frame && (frame.reviewing || frame.activeTab !== tab)) return null;
  return <>{children}</>;
}

/**
 * The component's save/discard controls. Inside the frame they move into its
 * sticky footer; standalone they render in place. Submit buttons portalled
 * out of their form need `form="<form id>"`.
 */
export function PanelFooter({ children }: { children: ReactNode }) {
  const frame = useContext(PanelFrameContext);
  if (!frame) return <>{children}</>;
  return frame.footer ? createPortal(children, frame.footer) : null;
}

/**
 * The selected row's details and controls. On a tab the frame gives an
 * inspector they show there; standalone they render in place.
 */
export const PanelInspector = InspectorPortal;

/** Reports the component's unsaved-change count to the frame (drives the leave guard). */
export function usePanelDirty(count: number) {
  const frame = useContext(PanelFrameContext);
  const setDirty = frame?.setDirty;
  useEffect(() => {
    if (!setDirty) return;
    setDirty(count);
    return () => setDirty(0);
  }, [setDirty, count]);
}

/** While true, the frame hides its tabs and sections so the review screen replaces the body. */
export function usePanelReviewing(reviewing: boolean) {
  const frame = useContext(PanelFrameContext);
  const setReviewing = frame?.setReviewing;
  useEffect(() => {
    if (!setReviewing) return;
    setReviewing(reviewing);
    return () => setReviewing(false);
  }, [setReviewing, reviewing]);
}

export interface PanelTab {
  id: string;
  label: string;
  /** The tab is a table whose selected row shows in the inspector. */
  inspector?: boolean;
}

export interface SettingsPanelFrameProps {
  title: string;
  /** What the feature does, under the title. */
  description?: ReactNode;
  /** Status badge shown beside the title. */
  status?: ReactNode;
  /** The chip bar: one chip per feature. */
  chips?: ReactNode;
  /** Header figures for the feature. */
  stats?: ReactNode;
  /** The feature's quick action, at the header's right. */
  actions?: ReactNode;
  tabs: readonly PanelTab[];
  activeTab: string;
  onTabChange: (tab: string) => void;
  /** Reports the unsaved-change count, so the page can guard leaving. */
  onDirtyChange: (count: number) => void;
  /** When saved changes take effect, e.g. "Applies within 30 s". */
  applies: string;
  children: ReactNode;
}

/** Chips, header, tab strip, body, inspector and sticky save bar around one settings component. */
export function SettingsPanelFrame({
  title,
  description,
  status,
  chips,
  stats,
  actions,
  tabs,
  activeTab,
  onTabChange,
  onDirtyChange,
  applies,
  children,
}: SettingsPanelFrameProps) {
  const [footer, setFooter] = useState<HTMLElement | null>(null);
  const [dirty, setDirty] = useState(0);
  const [reviewing, setReviewing] = useState(false);

  useEffect(() => onDirtyChange(dirty), [dirty, onDirtyChange]);

  const value = useMemo<PanelFrameValue>(
    () => ({ activeTab, reviewing, footer, setDirty, setReviewing }),
    [activeTab, reviewing, footer],
  );

  return (
    <PanelFrameContext.Provider value={value}>
      <Workspace
        title={title}
        pill={status}
        subtitle={description}
        chipsLabel={chips ? "Settings" : undefined}
        chips={chips}
        stats={stats}
        actions={actions}
        tabs={
          tabs.length > 1 && !reviewing ? (
            <WorkspaceTabs label={`${title} sections`} tabs={tabs} active={activeTab} onChange={onTabChange} />
          ) : undefined
        }
        inspectorSlot={!reviewing && !!tabs.find((t) => t.id === activeTab)?.inspector}
        footer={
          <footer className="flex flex-wrap items-center gap-3 px-7 py-3">
            <p className="m-0 mr-auto text-sm text-muted-foreground">
              {dirty > 0 ? (
                <b className="font-semibold text-foreground">
                  {dirty} unsaved {dirty === 1 ? "change" : "changes"}
                </b>
              ) : (
                "No unsaved changes"
              )}
              {" · "}
              {applies}
            </p>
            <div ref={setFooter} className="flex flex-wrap items-center justify-end gap-2" />
          </footer>
        }
      >
        {children}
      </Workspace>
    </PanelFrameContext.Provider>
  );
}
