import { describe, expect, it } from "vitest";
import { SwaggerException } from "api-client";
import { describeOperationError } from "./operation-error";

function swagger(status: number, response: string, message = "An unexpected server error occurred.") {
  return new SwaggerException(message, status, response, {}, null);
}

describe("describeOperationError", () => {
  it("uses a ProblemDetails detail", () => {
    const text = describeOperationError(swagger(500, JSON.stringify({ title: "Oops", detail: "The store is down" })));

    expect(text).toContain("500");
    expect(text).toContain("The store is down");
    expect(text).not.toContain("Oops");
  });

  it("falls back to a ProblemDetails title", () => {
    expect(describeOperationError(swagger(400, JSON.stringify({ title: "Bad request" })))).toContain("Bad request");
  });

  it("uses a JSON string body", () => {
    expect(describeOperationError(swagger(400, JSON.stringify("Needs Cosmos DB")))).toContain("Needs Cosmos DB");
  });

  it("uses a plain-text body", () => {
    expect(describeOperationError(swagger(400, "Needs Cosmos DB"))).toContain("Needs Cosmos DB");
  });

  it("falls back to the exception message when the body is empty", () => {
    const text = describeOperationError(swagger(502, "", "Bad gateway"));

    expect(text).toContain("502");
    expect(text).toContain("Bad gateway");
  });

  it("says a 403 needs the site Owner role and keeps the status", () => {
    const text = describeOperationError(swagger(403, "", "Forbidden"));

    expect(text).toContain("403");
    expect(text).toContain("Owner");
  });

  it("describes a failure without a status", () => {
    expect(describeOperationError(new TypeError("Failed to fetch"))).toMatch(/request failed.*Failed to fetch/i);
    expect(describeOperationError(undefined)).toBe("The request failed.");
  });
});
