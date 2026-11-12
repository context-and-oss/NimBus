import { describe, expect, it } from "vitest";
import * as api from "./index";
import { errorMessage, isAntiforgeryRefusal, problemDetail, problemOf } from "./problem";

const problemBody = JSON.stringify({
  type: "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  title: "Not Found",
  status: 404,
  detail: "Event not found",
  traceId: "00-abc",
});

describe("problemOf", () => {
  it("reads the parsed problem the generated client puts on .result", () => {
    const result = new api.ProblemDetails({ status: 409, title: "Conflict", code: "StillActive", errors: ["Revoke first."] });
    const error = new api.SwaggerException("Conflict", 409, "", {}, result);
    expect(problemOf(error)?.code).toBe("StillActive");
  });

  it("falls back to the raw response body", () => {
    const error = new api.SwaggerException("Not Found", 404, problemBody, {}, null);
    expect(problemOf(error)?.detail).toBe("Event not found");
  });

  it("is undefined for a body that is not a problem", () => {
    expect(problemOf(new api.SwaggerException("Bad Request", 400, "plain text", {}, null))).toBeUndefined();
  });
});

describe("problemDetail", () => {
  it("prefers detail, then errors, then title", () => {
    expect(problemDetail(problemBody)).toBe("Event not found");
    expect(problemDetail({ title: "Bad Request", errors: ["Name is required.", "Too long."] })).toBe("Name is required. Too long.");
    expect(problemDetail({ title: "Forbidden" })).toBe("Forbidden");
  });

  it("still reads a JSON string or plain text body", () => {
    expect(problemDetail('"Endpoint not found"')).toBe("Endpoint not found");
    expect(problemDetail("Endpoint not found")).toBe("Endpoint not found");
    expect(problemDetail("  ")).toBeUndefined();
  });

  it("cuts long text", () => {
    expect(problemDetail({ detail: "x".repeat(600) })?.length).toBe(501);
  });
});

describe("errorMessage", () => {
  it("uses the server's detail over the reason phrase", () => {
    expect(errorMessage(new api.SwaggerException("Not Found", 404, problemBody, {}, null))).toBe("Event not found");
  });

  it("uses the error's message when the body says nothing", () => {
    expect(errorMessage(new api.SwaggerException("Bad Gateway", 502, "", {}, null))).toBe("Bad Gateway");
    expect(errorMessage(new Error("offline"))).toBe("offline");
  });
});

describe("isAntiforgeryRefusal", () => {
  it("is true only for a 400 carrying the antiforgery code", () => {
    expect(isAntiforgeryRefusal(400, { code: "InvalidAntiforgeryToken" })).toBe(true);
    expect(isAntiforgeryRefusal(400, { code: "Invalid" })).toBe(false);
    expect(isAntiforgeryRefusal(400, undefined)).toBe(false);
    expect(isAntiforgeryRefusal(409, { code: "InvalidAntiforgeryToken" })).toBe(false);
  });
});
