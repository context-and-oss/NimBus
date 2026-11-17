import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import AccessControl from "./access-control";

const mocks = vi.hoisted(() => {
  interface PendingRequest {
    endpointId: string;
    resolve: (value: unknown) => void;
  }

  const pendingLoads: PendingRequest[] = [];
  const pendingAdds: PendingRequest[] = [];

  const defer = (queue: PendingRequest[], endpointId: string) =>
    new Promise<unknown>((resolve) => {
      queue.push({ endpointId, resolve });
    });

  const endpointOwner = {
    canManageAccessControl: false,
    email: "owner@example.test",
    endpointRoles: [
      { endpointId: "endpoint-a", role: "owner" },
      { endpointId: "endpoint-b", role: "owner" },
    ],
  };

  return {
    endpointOwner,
    access: endpointOwner as Record<string, unknown>,
    pendingLoads,
    pendingAdds,
    getEndpointAccessControl: vi.fn((endpointId: string) =>
      defer(pendingLoads, endpointId),
    ),
    postEndpointAccessControlRole: vi.fn(
      (endpointId: string, _entry: unknown) => defer(pendingAdds, endpointId),
    ),
    deleteEndpointAccessControlRole: vi.fn(),
    getAccessControl: vi.fn(),
    getEndpointsAll: vi.fn(),
    postAccessControlRole: vi.fn(),
    deleteAccessControlRole: vi.fn(),
    invalidateAccess: vi.fn(),
  };
});

vi.mock("hooks/use-access", async (importOriginal) => ({
  // Only the network-backed hook is stubbed; isOwnerRole stays real so the page
  // resolves endpoint ownership the same way it does against a live server.
  ...(await importOriginal<typeof import("hooks/use-access")>()),
  useAccess: () => ({ access: mocks.access }),
  invalidateAccess: mocks.invalidateAccess,
}));

vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: vi.fn() }),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");

  class FakeClient {
    getEndpointAccessControl = mocks.getEndpointAccessControl;
    postEndpointAccessControlRole = mocks.postEndpointAccessControlRole;
    deleteEndpointAccessControlRole = mocks.deleteEndpointAccessControlRole;
    getAccessControl = mocks.getAccessControl;
    getEndpointsAll = mocks.getEndpointsAll;
    postAccessControlRole = mocks.postAccessControlRole;
    deleteAccessControlRole = mocks.deleteAccessControlRole;
  }

  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const accessSet = (reader: string) =>
  new api.AccessControlSet({ readers: [reader] });

const renderPage = async () => {
  render(
    <MemoryRouter>
      <AccessControl />
    </MemoryRouter>,
  );
};

const scopes = () => screen.getByRole("navigation", { name: "Access scopes" });

const selectEndpoint = (endpointId: string) => {
  fireEvent.click(within(scopes()).getByRole("button", { name: endpointId }));
};

const addGrant = (entry: string, role?: string) => {
  fireEvent.change(screen.getByLabelText("Principal"), { target: { value: entry } });
  if (role) fireEvent.click(screen.getByRole("radio", { name: role }));
  fireEvent.click(screen.getByRole("button", { name: "Add grant" }));
};

const resolveLoad = async (endpointId: string, reader: string) => {
  const request = mocks.pendingLoads.find(
    (pending) => pending.endpointId === endpointId,
  );
  expect(request).toBeDefined();
  await act(async () => {
    request!.resolve(accessSet(reader));
  });
};

beforeEach(() => {
  mocks.access = mocks.endpointOwner;
  mocks.pendingLoads.length = 0;
  mocks.pendingAdds.length = 0;
  mocks.getEndpointAccessControl.mockClear();
  mocks.postEndpointAccessControlRole.mockClear();
  mocks.deleteEndpointAccessControlRole.mockReset();
  mocks.getAccessControl.mockReset();
  mocks.getEndpointsAll.mockReset();
  mocks.postAccessControlRole.mockReset();
  mocks.deleteAccessControlRole.mockReset();
  mocks.invalidateAccess.mockClear();
});

afterEach(() => {
  cleanup();
});

describe("AccessControl endpoint race safety", () => {
  it("drops endpoint A's slow load response after endpoint B is selected", async () => {
    await renderPage();

    selectEndpoint("endpoint-a");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-a"),
    );

    selectEndpoint("endpoint-b");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-b"),
    );

    await resolveLoad("endpoint-b", "reader-b@example.test");
    await resolveLoad("endpoint-a", "reader-a@example.test");

    expect(screen.getByText("reader-b@example.test")).toBeTruthy();
    expect(screen.queryByText("reader-a@example.test")).toBeNull();
  });

  it("does not commit endpoint A's mutation response after switching to endpoint B", async () => {
    await renderPage();

    selectEndpoint("endpoint-a");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-a"),
    );
    await resolveLoad("endpoint-a", "reader-a@example.test");

    addGrant("new-reader-a@example.test");

    await waitFor(() =>
      expect(mocks.postEndpointAccessControlRole).toHaveBeenCalledTimes(1),
    );
    expect(mocks.pendingAdds[0].endpointId).toBe("endpoint-a");

    selectEndpoint("endpoint-b");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-b"),
    );
    await resolveLoad("endpoint-b", "reader-b@example.test");

    await act(async () => {
      mocks.pendingAdds[0].resolve(accessSet("new-reader-a@example.test"));
    });

    expect(screen.getByText("reader-b@example.test")).toBeTruthy();
    expect(screen.queryByText("new-reader-a@example.test")).toBeNull();
  });

  it("does not expose endpoint A's stale entries as mutations against endpoint B", async () => {
    mocks.deleteEndpointAccessControlRole.mockResolvedValue(
      accessSet("reader-b@example.test"),
    );
    await renderPage();

    selectEndpoint("endpoint-a");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-a"),
    );
    await resolveLoad("endpoint-a", "reader-a@example.test");

    selectEndpoint("endpoint-b");

    const staleRow = screen.queryByRole("button", { name: "reader-a@example.test" });
    if (staleRow) {
      fireEvent.click(staleRow);
      fireEvent.click(screen.getByRole("button", { name: "Remove grant…" }));
      fireEvent.click(
        screen.getByRole("button", { name: "Remove reader-a@example.test from Reader" }),
      );
    }

    expect(mocks.deleteEndpointAccessControlRole).not.toHaveBeenCalled();
    expect(screen.queryByText("reader-a@example.test")).toBeNull();
  });

  it("keeps a refreshed endpoint A load over an older endpoint A mutation", async () => {
    await renderPage();

    selectEndpoint("endpoint-a");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-a"),
    );
    await resolveLoad("endpoint-a", "reader-a@example.test");

    addGrant("old-mutation@example.test");
    await waitFor(() =>
      expect(mocks.postEndpointAccessControlRole).toHaveBeenCalledTimes(1),
    );

    selectEndpoint("endpoint-b");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledWith("endpoint-b"),
    );
    selectEndpoint("endpoint-a");
    await waitFor(() =>
      expect(mocks.getEndpointAccessControl).toHaveBeenCalledTimes(3),
    );

    const refreshedLoad = mocks.pendingLoads[mocks.pendingLoads.length - 1];
    expect(refreshedLoad.endpointId).toBe("endpoint-a");
    await act(async () => {
      refreshedLoad.resolve(accessSet("fresh-reader-a@example.test"));
    });
    await act(async () => {
      mocks.pendingAdds[0].resolve(accessSet("old-mutation@example.test"));
    });

    expect(screen.getByText("fresh-reader-a@example.test")).toBeTruthy();
    expect(screen.queryByText("old-mutation@example.test")).toBeNull();
  });
});

describe("AccessControl scopes and grants", () => {
  it("asks an endpoint Owner to pick one of their endpoints first", async () => {
    await renderPage();

    expect(screen.getByText("Select an endpoint to see and change its grants.")).toBeTruthy();
    expect(within(scopes()).queryByRole("button", { name: "Site roles" })).toBeNull();
    expect(mocks.getEndpointAccessControl).not.toHaveBeenCalled();
  });

  describe("as a site Owner", () => {
    const site = new api.AccessControlSet({
      readers: ["ops@example.test"],
      contributors: ["agent:enrichment"],
      owners: ["owner@example.test"],
      piiReaders: ["owner@example.test"],
    });

    beforeEach(() => {
      mocks.access = { ...mocks.endpointOwner, canManageAccessControl: true, endpointRoles: [] };
      mocks.getAccessControl.mockResolvedValue(site);
      mocks.getEndpointsAll.mockResolvedValue(["endpoint-a"]);
    });

    it("opens on the site roles as one table, one row per entry and role", async () => {
      await renderPage();

      const table = await screen.findByRole("table", { name: "Site grants" });
      await waitFor(() => expect(within(table).getAllByRole("row")).toHaveLength(5));
      expect(within(scopes()).getByRole("button", { name: "Site roles" }).getAttribute("aria-current")).toBe("true");
      const agentRow = within(table).getByRole("button", { name: "agent:enrichment" }).closest("tr")!;
      expect(agentRow.textContent).toContain("Agent");
      expect(agentRow.textContent).toContain("Contributor");
      expect(within(table).getAllByRole("button", { name: "owner@example.test" })).toHaveLength(2);
    });

    it("adds a grant with the chosen role to the site", async () => {
      mocks.postAccessControlRole.mockResolvedValue(site);
      await renderPage();
      await screen.findByRole("button", { name: "ops@example.test" });

      addGrant("new@example.test", "Contributor");

      await waitFor(() => expect(mocks.postAccessControlRole).toHaveBeenCalledTimes(1));
      const entry = mocks.postAccessControlRole.mock.calls[0][0] as api.RoleEntry;
      expect(entry.entry).toBe("new@example.test");
      expect(entry.role).toBe(api.RoleEntryRole.Contributor);
    });

    it("refuses an agent key as Owner before sending it", async () => {
      await renderPage();
      await screen.findByRole("button", { name: "ops@example.test" });

      fireEvent.change(screen.getByLabelText("Principal"), { target: { value: "agent:x" } });
      fireEvent.click(screen.getByRole("radio", { name: "Owner" }));

      expect(screen.getByRole("alert").textContent).toContain("Reader or Contributor");
      expect((screen.getByRole("button", { name: "Add grant" }) as HTMLButtonElement).disabled).toBe(true);
    });

    it("removes a grant only after it is confirmed, and warns before removing your own Owner grant", async () => {
      mocks.deleteAccessControlRole.mockResolvedValue(site);
      await renderPage();
      const table = await screen.findByRole("table", { name: "Site grants" });

      const ownerRow = within(table)
        .getAllByRole("button", { name: "owner@example.test" })
        .find((b) => b.closest("tr")!.textContent!.includes("Owner") && !b.closest("tr")!.textContent!.includes("PII"))!;
      fireEvent.click(ownerRow);
      expect(screen.getByText(/your own Owner grant/)).toBeTruthy();

      fireEvent.click(screen.getByRole("button", { name: "Remove grant…" }));
      expect(mocks.deleteAccessControlRole).not.toHaveBeenCalled();
      fireEvent.click(screen.getByRole("button", { name: "Remove owner@example.test from Owner" }));

      await waitFor(() => expect(mocks.deleteAccessControlRole).toHaveBeenCalledTimes(1));
      expect((mocks.deleteAccessControlRole.mock.calls[0][0] as api.RoleEntry).role).toBe(api.RoleEntryRole.Owner);
    });
  });
});
