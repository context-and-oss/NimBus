import {
  createContext,
  useContext,
  useEffect,
  useState,
  useCallback,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";
import { cn } from "lib/utils";

export type ToastVariant = "success" | "error" | "warning" | "info";

export interface Toast {
  id: string;
  title?: string;
  description?: string;
  variant?: ToastVariant;
  duration?: number;
  /** Optional inline action (e.g. "Undo"). Dismisses the toast on click. */
  action?: { label: string; onClick: () => void };
  /** Shows a bar that drains over `duration`, for a toast that counts down to something. */
  progress?: boolean;
}

interface ToastContextType {
  toasts: Toast[];
  addToast: (toast: Omit<Toast, "id">) => void;
  removeToast: (id: string) => void;
}

const ToastContext = createContext<ToastContextType | undefined>(undefined);

// Imperative emitter so non-React callers (class components, async catch
// blocks, top-level error handlers) can show toasts without needing the
// context. ToastProvider registers itself here on mount; if nothing has
// registered yet, calls fall through to console.warn so toasts don't
// silently drop in tests / non-UI contexts.
type ToastEmitter = (toast: Omit<Toast, "id">) => void;
let imperativeEmit: ToastEmitter | null = null;

export const emitToast: ToastEmitter = (toast) => {
  if (imperativeEmit) {
    imperativeEmit(toast);
  } else if (typeof console !== "undefined") {
    console.warn(
      "[toast]",
      toast.variant ?? "info",
      toast.title ?? "",
      toast.description ?? "",
    );
  }
};

export const useToast = () => {
  const context = useContext(ToastContext);
  if (!context) {
    throw new Error("useToast must be used within a ToastProvider");
  }
  return context;
};

export const ToastProvider = ({ children }: { children: ReactNode }) => {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const addToast = useCallback((toast: Omit<Toast, "id">) => {
    const id = Math.random().toString(36).substr(2, 9);
    const newToast = { ...toast, id };
    setToasts((prev) => [...prev, newToast]);

    // Auto-remove after duration
    const duration = toast.duration ?? 5000;
    if (duration > 0) {
      setTimeout(() => {
        setToasts((prev) => prev.filter((t) => t.id !== id));
      }, duration);
    }
  }, []);

  const removeToast = useCallback((id: string) => {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }, []);

  useEffect(() => {
    imperativeEmit = addToast;
    return () => {
      if (imperativeEmit === addToast) imperativeEmit = null;
    };
  }, [addToast]);

  return (
    <ToastContext.Provider value={{ toasts, addToast, removeToast }}>
      {children}
      <ToastContainer toasts={toasts} removeToast={removeToast} />
    </ToastContext.Provider>
  );
};

const ToastContainer = ({
  toasts,
  removeToast,
}: {
  toasts: Toast[];
  removeToast: (id: string) => void;
}) => {
  if (toasts.length === 0) return null;

  return createPortal(
    <div className="fixed bottom-4 right-4 z-50 flex flex-col gap-2 max-w-sm">
      {toasts.map((toast) => (
        <ToastItem
          key={toast.id}
          toast={toast}
          onClose={() => removeToast(toast.id)}
        />
      ))}
    </div>,
    document.body,
  );
};

const ToastItem = ({
  toast,
  onClose,
}: {
  toast: Toast;
  onClose: () => void;
}) => {
  // Same paired tint/ink/dark-override convention as every other status surface in the app (see
  // badge.tsx, admin/operations.tsx, confirm-destructive-action.tsx) — the previous raw Tailwind
  // literals (bg-red-50 + text-foreground) paired a background that never changed with theme-aware
  // foreground text, so in dark mode near-white text landed on a near-white background.
  const variants: Record<
    ToastVariant,
    { bg: string; text: string; icon: ReactNode }
  > = {
    success: {
      bg: "bg-status-success-50 border-status-success/30 dark:bg-green-950/30 dark:border-green-900/60",
      text: "text-status-success-ink dark:text-green-200",
      icon: (
        <svg
          className="w-5 h-5 text-status-success"
          fill="currentColor"
          viewBox="0 0 20 20"
        >
          <path
            fillRule="evenodd"
            d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z"
            clipRule="evenodd"
          />
        </svg>
      ),
    },
    error: {
      bg: "bg-status-danger-50 border-status-danger/30 dark:bg-red-950/30 dark:border-red-900/60",
      text: "text-status-danger-ink dark:text-red-200",
      icon: (
        <svg
          className="w-5 h-5 text-status-danger"
          fill="currentColor"
          viewBox="0 0 20 20"
        >
          <path
            fillRule="evenodd"
            d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.707 7.293a1 1 0 00-1.414 1.414L8.586 10l-1.293 1.293a1 1 0 101.414 1.414L10 11.414l1.293 1.293a1 1 0 001.414-1.414L11.414 10l1.293-1.293a1 1 0 00-1.414-1.414L10 8.586 8.707 7.293z"
            clipRule="evenodd"
          />
        </svg>
      ),
    },
    warning: {
      bg: "bg-status-warning-50 border-status-warning/30 dark:bg-yellow-950/30 dark:border-yellow-900/60",
      text: "text-status-warning-ink dark:text-yellow-200",
      icon: (
        <svg
          className="w-5 h-5 text-status-warning"
          fill="currentColor"
          viewBox="0 0 20 20"
        >
          <path
            fillRule="evenodd"
            d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z"
            clipRule="evenodd"
          />
        </svg>
      ),
    },
    info: {
      bg: "bg-status-info-50 border-status-info/30 dark:bg-blue-950/30 dark:border-blue-900/60",
      text: "text-status-info-ink dark:text-blue-200",
      icon: (
        <svg
          className="w-5 h-5 text-status-info"
          fill="currentColor"
          viewBox="0 0 20 20"
        >
          <path
            fillRule="evenodd"
            d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a1 1 0 000 2v3a1 1 0 001 1h1a1 1 0 100-2v-3a1 1 0 00-1-1H9z"
            clipRule="evenodd"
          />
        </svg>
      ),
    },
  };

  const variant = toast.variant ?? "info";
  const { bg, text, icon } = variants[variant];

  return (
    <div
      className={cn(
        "flex items-start gap-3 p-4 rounded-lg border shadow-lg",
        "animate-slide-in-from-right",
        bg,
      )}
      role="alert"
    >
      <div className="shrink-0">{icon}</div>
      <div className="flex-1 min-w-0">
        {toast.title && (
          <p className={cn("text-sm font-medium", text)}>{toast.title}</p>
        )}
        {toast.description && (
          <p className={cn("text-sm mt-0.5 opacity-90", text)}>
            {toast.description}
          </p>
        )}
        {toast.action && (
          <button
            type="button"
            onClick={() => {
              toast.action!.onClick();
              onClose();
            }}
            className="mt-1 font-mono text-xs font-semibold text-primary-600 underline-offset-2 hover:underline"
          >
            {toast.action.label}
          </button>
        )}
        {toast.progress && toast.duration ? (
          <ToastProgress duration={toast.duration} />
        ) : null}
      </div>
      <button
        onClick={onClose}
        className="shrink-0 p-1 text-muted-foreground hover:text-foreground rounded-sm"
      >
        <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 20 20">
          <path
            fillRule="evenodd"
            d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
            clipRule="evenodd"
          />
        </svg>
      </button>
    </div>
  );
};

// Drains from full to empty over the toast's duration: starts full, then transitions to empty
// on the next frame.
const ToastProgress = ({ duration }: { duration: number }) => {
  const [draining, setDraining] = useState(false);
  useEffect(() => {
    const frame = requestAnimationFrame(() => setDraining(true));
    return () => cancelAnimationFrame(frame);
  }, []);
  return (
    <div
      className="mt-2 h-1 overflow-hidden rounded-full bg-black/10 dark:bg-white/10"
      aria-hidden="true"
    >
      <div
        className="h-full rounded-full bg-current opacity-60"
        style={{
          width: draining ? "0%" : "100%",
          transition: `width ${duration}ms linear`,
        }}
      />
    </div>
  );
};

export { ToastContainer, ToastItem };
