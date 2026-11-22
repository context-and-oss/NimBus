import { execFileSync, spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const appHost = fileURLToPath(new URL("../../CrmErpDemo.AppHost/CrmErpDemo.AppHost.csproj", import.meta.url));
const aspire = process.platform === "win32" ? "aspire.exe" : "aspire";
if (!process.env.E2E__Key || process.env.E2E__Key.length < 32) throw new Error("Set E2E__Key to the same random key used to start the E2E AppHost.");
for (const resource of ["crm-api", "erp-api", "crm-adapter", "erp-adapter", "resolver", "nimbus-ops", "mailpit",
  "agent-zone", "enrichment-agent", "dataplatform-adapter"]) {
  execFileSync(aspire, ["wait", resource, "--apphost", appHost, "--timeout", "180", "--non-interactive"],
    { stdio: "inherit", timeout: 200_000, windowsHide: true });
}
const description = JSON.parse(execFileSync(aspire, ["describe", "--apphost", appHost, "--format", "Json", "--non-interactive"],
  { encoding: "utf8", timeout: 30_000, windowsHide: true }));
for (const [resourceName, variable] of [["crm-api", "CRM_API_URL"], ["erp-api", "ERP_API_URL"], ["nimbus-ops", "NIMBUS_OPS_URL"],
  ["mailpit", "MAILPIT_URL"]]) {
  const resource = description.resources.find((candidate: { displayName: string }) => candidate.displayName === resourceName);
  const url = resource?.urls.find((candidate: { name: string }) => candidate.name === "http")?.url;
  if (!url) throw new Error(`Aspire did not advertise an HTTP URL for ${resourceName}.`);
  process.env[variable] = url;
}
process.env.E2E_APPHOST = appHost;
process.env.PROPAGATION_TIMEOUT_MS ??= "120000";
const cli = fileURLToPath(new URL("../node_modules/@playwright/test/cli.js", import.meta.url));
const child = spawn(process.execPath, [cli, "test", ...process.argv.slice(2)],
  { cwd: fileURLToPath(new URL("..", import.meta.url)), stdio: "inherit", env: process.env, windowsHide: true });
process.exitCode = await new Promise<number>((resolve, reject) => {
  child.once("error", reject);
  child.once("exit", code => resolve(code ?? 1));
});
