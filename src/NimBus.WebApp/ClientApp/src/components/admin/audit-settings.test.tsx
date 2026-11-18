import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import {
  cleanup,
  render,
  screen,
  fireEvent,
  waitFor,
  within,
} from "@testing-library/react";
import AuditSettings from "./audit-settings";
import { renderInPanel } from "../../test-utils/render-in-panel";

const mocks = vi.hoisted(() => ({
  getAdminAuditSettings: vi.fn(),
  putAdminAuditSettings: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");
  class FakeClient {
    getAdminAuditSettings = mocks.getAdminAuditSettings;
    putAdminAuditSettings = mocks.putAdminAuditSettings;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const configurable = [
  "searchEvents",
  "getEventDetails",
  "resubmit",
  "failureClassified",
  "someFutureType",
];

beforeEach(() => {
  mocks.getAdminAuditSettings.mockReset().mockResolvedValue({
    disabledAuditTypes: ["getEventDetails"],
    configurableAuditTypes: configurable,
  });
  mocks.putAdminAuditSettings
    .mockReset()
    .mockImplementation((body: { disabledAuditTypes: string[] }) =>
      Promise.resolve({
        disabledAuditTypes: body.disabledAuditTypes,
        configurableAuditTypes: configurable,
      }),
    );
});

afterEach(() => {
  cleanup();
});

describe("AuditSettings", () => {
  it("lists every configurable type with its recorded state", async () => {
    render(<AuditSettings />);

    const search = await screen.findByRole("switch", {
      name: "Record Search events",
    });
    expect(search.getAttribute("aria-checked")).toBe("true");
    expect(
      screen
        .getByRole("switch", { name: "Record Get event details" })
        .getAttribute("aria-checked"),
    ).toBe("false");
    // A type the UI does not group yet still shows up rather than vanishing.
    expect(
      within(screen.getByRole("row", { name: /Some future type/ })).getByText("Other"),
    ).toBeTruthy();
    expect(screen.getByText("4 of 5 action types recorded.")).toBeTruthy();
  });

  it("lists Copy endpoint data under Message operations, not Other", async () => {
    mocks.getAdminAuditSettings.mockResolvedValue({
      disabledAuditTypes: [],
      configurableAuditTypes: ["purgeMessages", "copyEndpointData"],
    });
    render(<AuditSettings />);

    await screen.findByRole("switch", { name: "Record Copy endpoint data" });
    expect(
      within(screen.getByRole("row", { name: /Copy endpoint data/ })).getByText("Message operations"),
    ).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Other" })).toBeNull();
  });

  it("saves the disabled types and only enables Save when something changed", async () => {
    render(<AuditSettings />);

    const save = await screen.findByRole("button", { name: "Save" });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    fireEvent.click(screen.getByRole("switch", { name: "Record Search events" }));
    expect((save as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(save);

    await waitFor(() => expect(mocks.putAdminAuditSettings).toHaveBeenCalled());
    const body = mocks.putAdminAuditSettings.mock.calls[0][0];
    expect([...body.disabledAuditTypes].sort()).toEqual([
      "getEventDetails",
      "searchEvents",
    ]);
    await waitFor(() =>
      expect((save as HTMLButtonElement).disabled).toBe(true),
    );
  });

  it("switches a whole group with Record none", async () => {
    render(<AuditSettings />);

    // The group's action shows once its chip is chosen.
    fireEvent.click(await screen.findByRole("button", { name: "Browsing" }));
    // Browsing has one type off, so its group action offers Record all.
    fireEvent.click(screen.getByRole("button", { name: "Record all" }));
    fireEvent.click(screen.getByRole("button", { name: "Record none" }));
    // Only Browsing changed: Resubmit, in another group, is still recorded.
    fireEvent.click(screen.getByRole("button", { name: "All" }));
    expect(screen.getByRole("switch", { name: "Record Resubmit" }).getAttribute("aria-checked")).toBe("true");

    for (const name of ["Record Search events", "Record Get event details"]) {
      expect(
        screen.getByRole("switch", { name }).getAttribute("aria-checked"),
      ).toBe("false");
    }
  });

  it("filters the table by the chosen group and offers no group action for All", async () => {
    render(<AuditSettings />);

    await screen.findByRole("switch", { name: "Record Search events" });
    const chips = screen.getByRole("group", { name: "Group" });
    expect(within(chips).getByRole("button", { name: "All" }).getAttribute("aria-pressed")).toBe("true");
    expect(screen.queryByRole("button", { name: /^Record (all|none)$/ })).toBeNull();
    const table = screen.getByRole("table", { name: "Action types" });
    expect(within(table).getAllByRole("switch")).toHaveLength(5);

    fireEvent.click(within(chips).getByRole("button", { name: "Message operations" }));

    expect(within(chips).getByRole("button", { name: "Message operations" }).getAttribute("aria-pressed")).toBe("true");
    expect(within(chips).getByRole("button", { name: "All" }).getAttribute("aria-pressed")).toBe("false");
    expect(within(table).getAllByRole("switch").map((s) => s.getAttribute("aria-label"))).toEqual([
      "Record Resubmit",
      "Record Failure classified",
    ]);
    // Both are recorded, so the group action offers to switch them off.
    expect(screen.getByRole("button", { name: "Record none" })).toBeTruthy();
    // The count still covers every type, not only the ones shown.
    expect(screen.getByText("4 of 5 action types recorded.")).toBeTruthy();
  });

  it("shows the selected action type in the inspector", async () => {
    render(<AuditSettings />);

    await screen.findByRole("switch", { name: "Record Search events" });
    const inspector = screen.getByRole("complementary", { name: "Inspector" });
    expect(within(inspector).getByText("Select an action type to see what recording it means.")).toBeTruthy();

    const select = screen.getByRole("button", { name: "Get event details" });
    fireEvent.click(select);

    expect(select.getAttribute("aria-pressed")).toBe("true");
    expect(within(inspector).getByRole("heading", { name: "Get event details" })).toBeTruthy();
    expect(within(inspector).getByText("Browsing")).toBeTruthy();
    expect(within(inspector).getByText("Not recorded")).toBeTruthy();
    // The row's switch is the only control for the type.
    expect(screen.getAllByRole("switch", { name: "Record Get event details" })).toHaveLength(1);

    fireEvent.click(screen.getByRole("switch", { name: "Record Get event details" }));
    expect(within(inspector).getByText("Recorded")).toBeTruthy();
  });

  it("marks a row whose switch differs from the saved value", async () => {
    render(<AuditSettings />);

    const search = await screen.findByRole("switch", { name: "Record Search events" });
    const row = screen.getByRole("row", { name: /Search events/ });
    expect(within(row).queryByText("Changed")).toBeNull();

    fireEvent.click(search);
    expect(search.getAttribute("aria-checked")).toBe("false");
    expect(within(row).getByText("Changed")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Discard changes" }));
    expect(search.getAttribute("aria-checked")).toBe("true");
    expect(within(row).queryByText("Changed")).toBeNull();
  });

  it("shows a load failure instead of an empty list", async () => {
    mocks.getAdminAuditSettings.mockReset().mockRejectedValue(new Error("Forbidden"));

    render(<AuditSettings />);

    expect((await screen.findByRole("alert")).textContent).toBe("Forbidden");
  });

  // Spec 038 slice 2: inside the Settings panel frame.
  it("reports unsaved changes to the panel and saves from its footer", async () => {
    const { onDirtyChange } = renderInPanel(<AuditSettings />, "audit");
    const footer = screen.getByRole("contentinfo");
    const search = await screen.findByRole("switch", { name: "Record Search events" });

    fireEvent.click(search);
    fireEvent.click(screen.getByRole("switch", { name: "Record Get event details" }));

    expect(onDirtyChange).toHaveBeenLastCalledWith(2);
    fireEvent.click(within(footer).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(onDirtyChange).toHaveBeenLastCalledWith(0));
    expect(mocks.putAdminAuditSettings).toHaveBeenCalledTimes(1);
  });

  it("shows the selected action type in the panel's inspector column", async () => {
    renderInPanel(<AuditSettings />, "audit");

    fireEvent.click(await screen.findByRole("button", { name: "Resubmit" }));

    const inspector = screen.getByRole("complementary", { name: "Inspector" });
    expect(within(inspector).getByRole("heading", { name: "Resubmit" })).toBeTruthy();
    expect(screen.getByRole("table", { name: "Action types" }).contains(inspector)).toBe(false);
  });
});
