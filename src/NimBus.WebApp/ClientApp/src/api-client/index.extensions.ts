export class AuthClient {
  constructor(
    private apiUrl?: string,
    private accessToken?: string,
  ) {}

  getBaseUrl(
    defaultUrl: string | undefined,
    requestedUrl?: string | undefined,
  ): string {
    return requestedUrl ? requestedUrl : (this.apiUrl ?? "");
  }

  transformHttpRequestOptions(options: RequestInit): Promise<RequestInit> {
    if (options.headers && this.accessToken) {
      options.headers = {
        ...options.headers,
        Authorization: "Bearer " + this.accessToken,
      };
    }

    return Promise.resolve(options);
  }
}

export const CookieAuth = () =>
  new AuthClient(
    `https://${window.location.hostname}:${window.location.port ?? ""}`,
    undefined,
  );

// The WebApp hands out its antiforgery request token in this readable cookie and requires it as
// this header on writes from a browser session (#296).
const CSRF_COOKIE = "NimBus.CSRF";
const CSRF_HEADER = "X-NimBus-CSRF";
const SAFE_METHODS = new Set(["GET", "HEAD", "OPTIONS", "TRACE"]);

function readCsrfToken(): string | undefined {
  const prefix = `${CSRF_COOKIE}=`;
  const cookie = document.cookie.split("; ").find((part) => part.startsWith(prefix));
  return cookie ? decodeURIComponent(cookie.slice(prefix.length)) : undefined;
}

export class ApiClientBase {
  constructor(private authClient: AuthClient) {}

  getBaseUrl(_: string | undefined, defaultUrl: string | undefined): string {
    return this.authClient
      ? this.authClient.getBaseUrl(defaultUrl)
      : defaultUrl!;
  }

  transformOptions(options: RequestInit): Promise<RequestInit> {
    if (!options.headers) {
      options.headers = {};
    }
    options.headers = { ...options.headers, "api-version": "2" };
    const csrfToken = SAFE_METHODS.has((options.method ?? "GET").toUpperCase())
      ? undefined
      : readCsrfToken();
    if (csrfToken) {
      options.headers = { ...options.headers, [CSRF_HEADER]: csrfToken };
    }
    const isIE = navigator.userAgent.indexOf("Trident/") !== -1;
    if (
      isIE &&
      (options.method === undefined ||
        options.method === "GET" ||
        options.method === "HEAD")
    ) {
      options.cache = "no-cache";
      options.headers = {
        ...options.headers,
        "X-IE-Cache-Bust": new Date().getTime().toString(),
      };
    }
    return this.authClient
      ? this.authClient.transformHttpRequestOptions(options)
      : Promise.resolve(options);
  }
}
