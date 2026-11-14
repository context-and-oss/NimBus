import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import { ToastProvider } from "components/ui";
import EndpointRowActions from "./endpoint-row-actions";

const apiMocks = vi.hoisted(() => ({
  postEndpointSubscriptionstatus: vi.fn(),
  getEndpointAccessControl: vi.fn(),
}));

vi.mock("api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("api-client")>();
  return {
    ...actual,
    CookieAuth: vi.fn(() => ({})),
    Client: vi.fn(function () {
      return apiMocks;
    }),
  };
});

// The row derives what it may offer from the current user's resolved access —
// stub it rather than the /accesscontrol/me round trip behind it.
const access = vi.hoisted(() => ({ current: null as unknown }));
vi.mock("hooks/use-access", async (importOriginal) => ({
  // Keep the real isOwnerRole — the casing it tolerates is part of what these
  // tests cover. Only the network-backed hook is stubbed.
  ...(await importOriginal<typeof import("hooks/use-access")>()),
  useAccess: () => ({ access: access.current }),
  invalidateAccess: vi.fn(),
}));

// Role values are spelled the way the SERVER sends them — PascalCase CLR names
// ("Owner"), not the lowercase values the OpenAPI spec and generated client
// declare. Verified live against /api/access-control/me. Keep these as-is: they
// are the regression guard for that mismatch.
const asOwnerOfAlice = () => {
  access.current = {
    siteRole: "Contributor",
    endpointRoles: [{ endpointId: "Alice", role: "Owner" }],
  };
};

const asReader = () => {
  access.current = {
    siteRole: "Reader",
    endpointRoles: [{ endpointId: "Alice", role: "Reader" }],
  };
};

const refreshEndpoint = vi.fn();

const renderActions = (subscriptionStatus = "active") =>
  render(
    <MemoryRouter>
      <ToastProvider>
        <EndpointRowActions
          endpointId="Alice"
          subscriptionStatus={subscriptionStatus}
          failed={2}
          deferred={0}
          pending={7}
          storageAvailable
          refreshEndpoint={refreshEndpoint}
          startLoading={vi.fn()}
          stopLoading={vi.fn()}
        />
      </ToastProvider>
    </MemoryRouter>,
  );

const openMenu = () =>
  userEvent.click(
    screen.getByRole("button", { name: /more actions for alice/i }),
  );

describe("EndpointRowActions", () => {
  beforeEach(() => {
    asOwnerOfAlice();
    apiMocks.postEndpointSubscriptionstatus.mockResolvedValue(undefined);
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("offers the owner-only items to an endpoint owner", async () => {
    renderActions();
    await openMenu();

    expect(screen.getByRole("menuitem", { name: /configure alerts/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /manage access/i })).toBeTruthy();
    // Purging moved to Operations → Delete all events, which also rebuilds the
    // endpoint's subscriptions; the row no longer offers a storage-only purge.
    expect(screen.queryByRole("menuitem", { name: /purge/i })).toBeNull();
    expect(
      screen.getByRole("switch", { name: /disable alice/i }).hasAttribute("disabled"),
    ).toBe(false);
  });

  it("recognises an owner grant whose endpoint id differs only by case", async () => {
    // Endpoint ids are matched case-insensitively server side, so a grant stored
    // as "alice" authorizes requests against "Alice" — the row must not hide the
    // controls the server would honour.
    access.current = {
      siteRole: "Reader",
      endpointRoles: [{ endpointId: "alice", role: "Owner" }],
    };
    renderActions();
    await openMenu();

    expect(screen.getByRole("menuitem", { name: /manage access/i })).toBeTruthy();
    expect(
      screen.getByRole("switch", { name: /disable alice/i }).hasAttribute("disabled"),
    ).toBe(false);
  });

  it("stops loading and reports when the row cannot be refreshed", async () => {
    // The disable already landed; only the read-back failed. Leaving that
    // rejection unhandled would strand the table in its loading state.
    const stopLoading = vi.fn();
    render(
      <MemoryRouter>
        <ToastProvider>
          <EndpointRowActions
            endpointId="Alice"
            subscriptionStatus="active"
            failed={0}
            deferred={0}
            pending={0}
            storageAvailable
            refreshEndpoint={() => Promise.reject(new Error("boom"))}
            startLoading={vi.fn()}
            stopLoading={stopLoading}
          />
        </ToastProvider>
      </MemoryRouter>,
    );

    await userEvent.click(screen.getByRole("switch", { name: /disable alice/i }));
    await userEvent.click(screen.getByRole("button", { name: /disable endpoint/i }));

    expect(apiMocks.postEndpointSubscriptionstatus).toHaveBeenCalledWith(
      "Alice",
      "disable",
    );
    await waitFor(() => expect(stopLoading).toHaveBeenCalled());
    expect(
      await screen.findByText(/could not be refreshed/i),
    ).toBeTruthy();
  });

  it("gives a reader no menu at all and locks the switch", async () => {
    asReader();
    renderActions();

    // Every item is Owner-only, so the trigger goes rather than opening an
    // empty menu. The row remains clickable for opening the endpoint.
    expect(
      screen.queryByRole("button", { name: /more actions for alice/i }),
    ).toBeNull();
    expect(
      screen.getByRole("switch", { name: /disable alice/i }).hasAttribute("disabled"),
    ).toBe(true);
  });

  it("confirms before disabling, then posts disable once", async () => {
    renderActions();

    await userEvent.click(screen.getByRole("switch", { name: /disable alice/i }));
    // Nothing is sent until the impact dialog is acknowledged.
    expect(apiMocks.postEndpointSubscriptionstatus).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole("button", { name: /disable endpoint/i }));

    expect(apiMocks.postEndpointSubscriptionstatus).toHaveBeenCalledTimes(1);
    expect(apiMocks.postEndpointSubscriptionstatus).toHaveBeenCalledWith(
      "Alice",
      "disable",
    );
    expect(refreshEndpoint).toHaveBeenCalledWith("Alice");
  });

  it("re-enables immediately, with no confirmation", async () => {
    renderActions("disabled");

    await userEvent.click(screen.getByRole("switch", { name: /enable alice/i }));

    expect(apiMocks.postEndpointSubscriptionstatus).toHaveBeenCalledWith(
      "Alice",
      "enable",
    );
  });

  it("leaves the switch dead when the subscription is missing", async () => {
    renderActions("not-found");

    const toggle = screen.getByRole("switch", { name: /enable alice/i });
    expect(toggle.hasAttribute("disabled")).toBe(true);
    await userEvent.click(toggle);
    expect(apiMocks.postEndpointSubscriptionstatus).not.toHaveBeenCalled();
  });
});
