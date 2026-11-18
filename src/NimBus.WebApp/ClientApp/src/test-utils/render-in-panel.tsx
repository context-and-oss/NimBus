import type { ReactNode } from "react";
import { render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { vi } from "vitest";
import { SettingsPanelFrame } from "components/settings/panel-frame";
import { SETTINGS_FEATURES } from "models/manage-pages";

/**
 * Renders a settings component inside the Settings frame, as
 * pages/settings.tsx does, on the given tab of the given feature.
 * Returns the spy that receives the frame's unsaved-change count.
 */
export function renderInPanel(ui: ReactNode, featureId: string, activeTab?: string) {
  const feature = SETTINGS_FEATURES.find((f) => f.id === featureId);
  if (!feature) throw new Error(`Unknown settings feature ${featureId}`);
  const onDirtyChange = vi.fn();
  const result = render(
    <MemoryRouter>
      <SettingsPanelFrame
        title={feature.name}
        tabs={feature.tabs}
        activeTab={activeTab ?? feature.tabs[0].id}
        onTabChange={vi.fn()}
        onDirtyChange={onDirtyChange}
        applies={feature.applies}
      >
        {ui}
      </SettingsPanelFrame>
    </MemoryRouter>,
  );
  return { ...result, onDirtyChange };
}
