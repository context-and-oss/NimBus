import { afterEach, describe, expect, it } from "vitest";
import { ApiClientBase, AuthClient } from "api-client";

// Issue #296: the WebApp hands out the antiforgery request token in the readable NimBus.CSRF cookie
// and requires it as the X-NimBus-CSRF header on writes from a browser session.
describe("ApiClientBase.transformOptions", () => {
  const client = new ApiClientBase(new AuthClient("https://nimbus.test", undefined));

  afterEach(() => {
    document.cookie = "NimBus.CSRF=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/";
  });

  async function headersFor(method: string): Promise<Record<string, string>> {
    const options = await client.transformOptions({ method, headers: { Accept: "application/json" } });
    return options.headers as Record<string, string>;
  }

  it.each(["POST", "PUT", "PATCH", "DELETE"])("sends the token on a %s", async (method) => {
    document.cookie = "NimBus.CSRF=token-1; path=/";

    const headers = await headersFor(method);

    expect(headers["X-NimBus-CSRF"]).toBe("token-1");
    expect(headers.Accept).toBe("application/json");
  });

  it("leaves reads alone", async () => {
    document.cookie = "NimBus.CSRF=token-1; path=/";

    expect((await headersFor("GET"))["X-NimBus-CSRF"]).toBeUndefined();
  });

  it("sends nothing before a token was issued", async () => {
    expect((await headersFor("POST"))["X-NimBus-CSRF"]).toBeUndefined();
  });

  it("reads the token among other cookies, decoded", async () => {
    document.cookie = "theme=dark; path=/";
    document.cookie = "NimBus.CSRF=CfDJ8%2Babc; path=/";

    expect((await headersFor("POST"))["X-NimBus-CSRF"]).toBe("CfDJ8+abc");
  });
});
