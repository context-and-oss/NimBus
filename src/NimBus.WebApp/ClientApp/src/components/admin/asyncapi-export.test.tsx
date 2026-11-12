import {
  describe,
  it,
  expect,
  afterEach,
  beforeEach,
  vi,
  type Mock,
} from "vitest";
import {
  cleanup,
  render,
  screen,
  fireEvent,
  waitFor,
} from "@testing-library/react";
import AsyncApiExport from "./asyncapi-export";

// The component downloads via a raw fetch (not the generated client) so the
// exact bytes and Content-Disposition/Content-Type headers are preserved, then
// saves the blob through a temporary object URL. These tests stub fetch, the
// URL object-url helpers (absent in jsdom), and capture the download anchor so
// we can assert the request format, the chosen filename, and object-URL cleanup.

let fetchMock: Mock;
let createObjectURL: Mock;
let revokeObjectURL: Mock;
let anchors: HTMLAnchorElement[];

const yamlResponse = (body = "asyncapi: 3.0.0\n") => ({
  ok: true,
  status: 200,
  blob: () => Promise.resolve(new Blob([body], { type: "application/x-yaml" })),
});

const jsonResponse = (body = '{"asyncapi":"3.0.0"}') => ({
  ok: true,
  status: 200,
  blob: () => Promise.resolve(new Blob([body], { type: "application/json" })),
});

beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);

  createObjectURL = vi.fn(() => "blob:nimbus-mock");
  revokeObjectURL = vi.fn();
  (URL as unknown as { createObjectURL: unknown }).createObjectURL =
    createObjectURL;
  (URL as unknown as { revokeObjectURL: unknown }).revokeObjectURL =
    revokeObjectURL;

  // Capture download anchors while delegating to the real createElement so
  // React's own DOM work is untouched. The anchor's click() is a no-op (jsdom
  // would otherwise try to navigate).
  anchors = [];
  const realCreate = document.createElement.bind(document);
  vi.spyOn(document, "createElement").mockImplementation(((tag: string) => {
    const el = realCreate(tag);
    if (tag === "a") {
      vi.spyOn(el as HTMLAnchorElement, "click").mockImplementation(() => {});
      anchors.push(el as HTMLAnchorElement);
    }
    return el;
  }) as typeof document.createElement);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("AsyncApiExport", () => {
  it("renders YAML and JSON download buttons", () => {
    render(<AsyncApiExport />);
    expect(
      screen.getByRole("button", { name: /download yaml/i }),
    ).toBeTruthy();
    expect(
      screen.getByRole("button", { name: /download json/i }),
    ).toBeTruthy();
  });

  it("downloads YAML: requests format=yaml, saves nimbus-asyncapi.yaml, and revokes the object URL", async () => {
    fetchMock.mockResolvedValue(yamlResponse());
    render(<AsyncApiExport />);

    fireEvent.click(screen.getByRole("button", { name: /download yaml/i }));

    await waitFor(() => expect(createObjectURL).toHaveBeenCalledTimes(1));

    // Raw fetch to the admin endpoint with the yaml format and credentials.
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toContain("/api/v1/admin/asyncapi");
    expect(url).toContain("format=yaml");
    expect(init).toMatchObject({ credentials: "include" });

    // The blob is turned into an object URL and saved under the yaml filename.
    expect(createObjectURL).toHaveBeenCalledWith(expect.any(Blob));
    const anchor = anchors[anchors.length - 1];
    expect(anchor.download).toBe("nimbus-asyncapi.yaml");
    expect(anchor.href).toContain("blob:nimbus-mock");

    // Object URL is always released after the download.
    expect(revokeObjectURL).toHaveBeenCalledWith("blob:nimbus-mock");
  });

  it("downloads JSON: requests format=json and saves nimbus-asyncapi.json", async () => {
    fetchMock.mockResolvedValue(jsonResponse());
    render(<AsyncApiExport />);

    fireEvent.click(screen.getByRole("button", { name: /download json/i }));

    await waitFor(() => expect(createObjectURL).toHaveBeenCalledTimes(1));

    const [url] = fetchMock.mock.calls[0];
    expect(url).toContain("format=json");
    const anchor = anchors[anchors.length - 1];
    expect(anchor.download).toBe("nimbus-asyncapi.json");
    expect(revokeObjectURL).toHaveBeenCalledWith("blob:nimbus-mock");
  });

  it("shows an error and never touches the object URL when the request is rejected", async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 403 });
    render(<AsyncApiExport />);

    fireEvent.click(screen.getByRole("button", { name: /download yaml/i }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toMatch(/permission/i);
    // No blob was ever created, so nothing to leak.
    expect(createObjectURL).not.toHaveBeenCalled();
    expect(revokeObjectURL).not.toHaveBeenCalled();
  });

  it("disables both buttons while a download is in flight", async () => {
    let resolveFetch: (value: unknown) => void = () => {};
    fetchMock.mockReturnValue(
      new Promise((resolve) => {
        resolveFetch = resolve;
      }),
    );
    render(<AsyncApiExport />);

    const yamlButton = screen.getByRole("button", { name: /download yaml/i });
    const jsonButton = screen.getByRole("button", { name: /download json/i });
    fireEvent.click(yamlButton);

    await waitFor(() =>
      expect((yamlButton as HTMLButtonElement).disabled).toBe(true),
    );
    expect((jsonButton as HTMLButtonElement).disabled).toBe(true);

    // Once the request completes, the buttons return to their idle state.
    resolveFetch(yamlResponse());
    await waitFor(() =>
      expect((yamlButton as HTMLButtonElement).disabled).toBe(false),
    );
  });
});
