import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ToastProvider } from "components/ui";
import EndpointAlertsModal from "./endpoint-alerts-modal";

const apiMocks = vi.hoisted(() => ({
  postEndpointSubscribe: vi.fn(),
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

vi.mock("hooks/event-types", () => ({
  getEventTypesByEndpoint: vi.fn(() => Promise.resolve({ consumes: [], produces: [] })),
}));

const clipboard = vi.hoisted(() => ({ copyToClipboard: vi.fn(() => Promise.resolve()) }));
vi.mock("lib/clipboard", () => clipboard);

const onClose = vi.fn();

const renderModal = () =>
  render(
    <ToastProvider>
      <EndpointAlertsModal endpointId="ErpEndpoint" isOpen onClose={onClose} />
    </ToastProvider>,
  );

const subscribeButton = () => screen.getByRole("button", { name: "Subscribe" }) as HTMLButtonElement;

describe("EndpointAlertsModal", () => {
  beforeEach(() => {
    apiMocks.postEndpointSubscribe.mockReset();
    clipboard.copyToClipboard.mockClear();
    onClose.mockReset();
  });

  afterEach(cleanup);

  it("offers windows of five and fifteen minutes next to hourly, daily and weekly", () => {
    renderModal();

    for (const label of ["Every 5 minutes", "Every 15 minutes", "Hourly", "Daily", "Weekly"]) {
      expect(screen.getByRole("radio", { name: label })).toBeTruthy();
    }
    expect((screen.getByRole("radio", { name: "Daily" }) as HTMLInputElement).checked).toBe(true);
  });

  it("accepts only an https webhook URL", async () => {
    const user = userEvent.setup();
    renderModal();
    await user.click(screen.getByRole("radio", { name: "Webhook" }));
    const url = screen.getByPlaceholderText("https://example.com/hooks/nimbus");

    await user.type(url, "http://agent.example.com/hook");
    expect(subscribeButton().disabled).toBe(true);

    await user.clear(url);
    await user.type(url, "https://agent.example.com/hook");
    expect(subscribeButton().disabled).toBe(false);
  });

  it("shows a webhook's signing secret once and closes only when it is dismissed", async () => {
    const user = userEvent.setup();
    apiMocks.postEndpointSubscribe.mockResolvedValue({ id: "sub-1", signingSecret: "nbwh_secret-value" });
    renderModal();
    await user.click(screen.getByRole("radio", { name: "Webhook" }));
    await user.type(screen.getByPlaceholderText("https://example.com/hooks/nimbus"), "https://agent.example.com/hook");
    await user.click(screen.getByRole("radio", { name: "Every 5 minutes" }));

    await user.click(subscribeButton());

    expect(await screen.findByText("nbwh_secret-value")).toBeTruthy();
    expect(screen.getByText(/won't be shown again/)).toBeTruthy();
    expect(onClose).not.toHaveBeenCalled();
    const body = apiMocks.postEndpointSubscribe.mock.calls[0][1];
    expect(body.type).toBe("webhook");
    expect(body.url).toBe("https://agent.example.com/hook");
    expect(body.mail).toBeUndefined();
    expect(body.frequency).toBe(300);

    await user.click(screen.getByRole("button", { name: "Copy secret" }));
    expect(clipboard.copyToClipboard).toHaveBeenCalledWith("nbwh_secret-value");

    await user.click(screen.getByRole("button", { name: "Done" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("closes straight away after a mail subscription", async () => {
    const user = userEvent.setup();
    apiMocks.postEndpointSubscribe.mockResolvedValue({ id: "sub-2" });
    renderModal();
    await user.type(screen.getByPlaceholderText("example@email.com"), "ops@example.com");

    await user.click(subscribeButton());

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(screen.queryByText(/won't be shown again/)).toBeNull();
    expect(apiMocks.postEndpointSubscribe.mock.calls[0][1].frequency).toBe(86400);
  });

  it("stays open and says why when the server refuses", async () => {
    const user = userEvent.setup();
    apiMocks.postEndpointSubscribe.mockRejectedValue(new Error("Alert webhooks are not configured on this deployment."));
    renderModal();
    await user.click(screen.getByRole("radio", { name: "Webhook" }));
    await user.type(screen.getByPlaceholderText("https://example.com/hooks/nimbus"), "https://agent.example.com/hook");

    await user.click(subscribeButton());

    expect(await screen.findByText("Alert webhooks are not configured on this deployment.")).toBeTruthy();
    expect(onClose).not.toHaveBeenCalled();
  });
});
