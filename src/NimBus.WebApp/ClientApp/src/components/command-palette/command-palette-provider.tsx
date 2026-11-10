import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { CommandPaletteContext } from "./command-palette-context";
import { CommandPalette } from "./command-palette";

interface CommandPaletteProviderProps {
  children: ReactNode;
  /**
   * Keeps the context but mounts no palette and no shortcut. The palette loads the caller's
   * access when it mounts, which a dashboard-token wall must not call.
   */
  disabled?: boolean;
}

/**
 * Owns the palette's open/close state and the global Cmd+K / Ctrl+K listener.
 *
 * The shortcut still fires when focus is in an `<input>` (operators paste GUIDs
 * mid-typing), but we opt out for `<textarea>` and `contenteditable` regions
 * where Ctrl/Cmd+K is more likely to mean "edit hyperlink".
 *
 * The palette itself is rendered once here so child trees only need to consume
 * the hook to open it — same pattern as `ToastProvider`.
 */
export const CommandPaletteProvider: React.FC<CommandPaletteProviderProps> = ({
  children,
  disabled = false,
}) => {
  const [isOpen, setIsOpen] = useState(false);

  const open = useCallback(() => setIsOpen(true), []);
  const close = useCallback(() => setIsOpen(false), []);
  const toggle = useCallback(() => setIsOpen((v) => !v), []);

  useEffect(() => {
    if (disabled) return;
    const handler = (e: KeyboardEvent) => {
      // Letter "k" only; ignore Shift+K and other modifiers.
      if (e.key !== "k" && e.key !== "K") return;
      if (!(e.metaKey || e.ctrlKey)) return;
      if (e.shiftKey || e.altKey) return;

      // Don't shadow textarea / rich-text editing shortcuts.
      const target = e.target as HTMLElement | null;
      if (target) {
        const tag = target.tagName;
        if (tag === "TEXTAREA") return;
        if (target.isContentEditable) return;
      }

      e.preventDefault();
      toggle();
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [toggle, disabled]);

  const value = useMemo(
    () => ({ isOpen, open, close, toggle }),
    [isOpen, open, close, toggle],
  );

  return (
    <CommandPaletteContext.Provider value={value}>
      {children}
      {!disabled && <CommandPalette isOpen={isOpen} onClose={close} />}
    </CommandPaletteContext.Provider>
  );
};
