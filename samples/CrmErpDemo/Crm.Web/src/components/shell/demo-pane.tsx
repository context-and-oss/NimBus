import { createContext, useCallback, useContext, useState, type ReactNode } from 'react';

// Open/closed state of the Demo controls pane. Open by default so a presenter (or a fresh
// Playwright browser) always sees the controls; closing it is remembered in this browser only.

const STORAGE_KEY = 'nimbus-demo.pane-open';

interface PaneState {
  open: boolean;
  setOpen: (open: boolean) => void;
  toggle: () => void;
}

const PaneContext = createContext<PaneState | null>(null);

function readStored(): boolean {
  try {
    return window.localStorage.getItem(STORAGE_KEY) !== 'false';
  } catch {
    return true;
  }
}

export function DemoPaneProvider({ children }: { children: ReactNode }) {
  const [open, setOpenState] = useState<boolean>(readStored);

  const setOpen = useCallback((next: boolean) => {
    setOpenState(next);
    try {
      window.localStorage.setItem(STORAGE_KEY, String(next));
    } catch {
      /* storage blocked: the choice lasts until reload */
    }
  }, []);

  const toggle = useCallback(() => setOpen(!open), [open, setOpen]);

  return <PaneContext.Provider value={{ open, setOpen, toggle }}>{children}</PaneContext.Provider>;
}

export function useDemoPane(): PaneState {
  const ctx = useContext(PaneContext);
  if (!ctx) throw new Error('useDemoPane must be used inside DemoPaneProvider');
  return ctx;
}
