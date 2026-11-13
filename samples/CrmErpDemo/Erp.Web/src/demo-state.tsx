import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api, type ErrorModeState, type FailureReason, type HandoffMode, type ModeState, type ProcessingDelay } from './api';

// One poll for the ERP demo modes, shared by the app-bar pills, the banners and the Demo
// controls pane. Handoff and delay are edited by their own pane sections (debounced) and read
// here for the pills; those sections call refresh() after a write so the pills follow quickly.

const POLL_MS = 3000;

interface ErpDemoState {
  serviceMode: ModeState | null;
  errorMode: ErrorModeState | null;
  reasons: FailureReason[];
  handoff: HandoffMode | null;
  delay: ProcessingDelay | null;
  busy: boolean;
  setServiceMode: (enabled: boolean) => Promise<void>;
  setErrorMode: (enabled: boolean, reason?: string) => Promise<void>;
  refresh: () => void;
}

const Ctx = createContext<ErpDemoState | null>(null);

export function ErpDemoStateProvider({ children }: { children: ReactNode }) {
  const [serviceMode, setService] = useState<ModeState | null>(null);
  const [errorMode, setError] = useState<ErrorModeState | null>(null);
  const [reasons, setReasons] = useState<FailureReason[]>([]);
  const [handoff, setHandoff] = useState<HandoffMode | null>(null);
  const [delay, setDelay] = useState<ProcessingDelay | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(() => {
    void Promise.allSettled([api.getServiceMode(), api.getErrorMode(), api.getHandoffMode(), api.getProcessingDelay()])
      .then(([s, e, h, d]) => {
        if (s.status === 'fulfilled') setService(s.value);
        if (e.status === 'fulfilled') setError(e.value);
        if (h.status === 'fulfilled') setHandoff(h.value);
        if (d.status === 'fulfilled') setDelay(d.value);
      });
  }, []);

  useEffect(() => {
    refresh();
    api.getErrorModeReasons().then(setReasons).catch(() => { /* the dropdown stays empty */ });
    const t = setInterval(refresh, POLL_MS);
    return () => clearInterval(t);
  }, [refresh]);

  const setServiceMode = useCallback(async (enabled: boolean) => {
    setBusy(true);
    try { setService(await api.setServiceMode(enabled)); } catch { /* the next poll reconciles */ } finally { setBusy(false); }
  }, []);

  const setErrorMode = useCallback(async (enabled: boolean, reason?: string) => {
    setBusy(true);
    try { setError(await api.setErrorMode(enabled, reason)); } catch { /* the next poll reconciles */ } finally { setBusy(false); }
  }, []);

  return (
    <Ctx.Provider value={{ serviceMode, errorMode, reasons, handoff, delay, busy, setServiceMode, setErrorMode, refresh }}>
      {children}
    </Ctx.Provider>
  );
}

export function useErpDemoState(): ErpDemoState {
  const ctx = useContext(Ctx);
  if (!ctx) throw new Error('useErpDemoState must be used inside ErpDemoStateProvider');
  return ctx;
}

export function formatDelay(ms: number): string {
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(ms % 1000 === 0 ? 0 : 1)} s`;
}
