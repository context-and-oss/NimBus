import { describe, expect, it } from "vitest";
import { problemMessage as agentKeyMessage } from "./agent-keys-model";
import { problemMessage as dashboardTokenMessage } from "./dashboard-tokens-model";

const problem = (status: number, body: Record<string, unknown>) =>
  Response.json({ status, ...body }, { status, headers: { "content-type": "application/problem+json" } });

describe.each([
  ["agent keys", agentKeyMessage],
  ["dashboard tokens", dashboardTokenMessage],
])("%s: refused write message", (_, problemMessage) => {
  it("explains an expired security token from the antiforgery problem", async () => {
    expect(await problemMessage(problem(400, { code: "InvalidAntiforgeryToken", title: "Bad Request" }))).toMatch(
      /security token expired/,
    );
  });

  it("shows the problem's errors", async () => {
    expect(await problemMessage(problem(400, { code: "Invalid", errors: ["Name is required."] }))).toBe("Name is required.");
  });

  it("falls back to the problem's detail, then the status", async () => {
    expect(await problemMessage(problem(409, { detail: "Try again." }))).toBe("Try again.");
    expect(await problemMessage(new Response("", { status: 500 }))).toBe("The request failed (HTTP 500).");
  });
});
