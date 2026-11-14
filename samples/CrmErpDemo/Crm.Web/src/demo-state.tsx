import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api, type CircuitStateSnapshot } from './api';

// One poll for the CRM demo modes, shared by the app-bar pills, the outage banner and the
// Demo controls pane. The outage toggle drives sustained handler failures on CrmEndpoint (the
// adapter's HTTP client returns synthetic 503s); the circuit state is pushed adapter → crm-api.

const POLL_MS = 3000;

interface CrmDemoState {
  /** CRM API outage; null until the first read succeeds. */
  outage: boolean | null;
  circuit: CircuitStateSnapshot | null;
  setOutage: (enabled: boolean) => Promise<void>;
}

const Ctx = createContext<CrmDemoState | null>(null);

export function CrmDemoStateProvider({ children }: { children: ReactNode }) {
  const [outage, setOutageState] = useState<boolean | null>(null);
  const [circuit, setCircuit] = useState<CircuitStateSnapshot | null>(null);

  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      const [mode, state] = await Promise.allSettled([api.getErrorMode(), api.getCircuitState()]);
      if (cancelled) return;
      if (mode.status === 'fulfilled') setOutageState(mode.value.enabled);
      if (state.status === 'fulfilled') setCircuit(state.value);
    };
    void tick();
    const t = setInterval(tick, POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(t);
    };
  }, []);

  const setOutage = useCallback(async (enabled: boolean) => {
    try {
      setOutageState((await api.setErrorMode(enabled)).enabled);
    } catch {
      /* the next poll reconciles */
    }
  }, []);

  return <Ctx.Provider value={{ outage, circuit, setOutage }}>{children}</Ctx.Provider>;
}

export function useCrmDemoState(): CrmDemoState {
  const ctx = useContext(Ctx);
  if (!ctx) throw new Error('useCrmDemoState must be used inside CrmDemoStateProvider');
  return ctx;
}
