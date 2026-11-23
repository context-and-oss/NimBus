import { AzureNamedKeyCredential, TableClient } from "@azure/data-tables";
import { test, expect } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { E2eControl } from "../helpers/e2e-control.js";
import { NimBusApiClient } from "../helpers/nimbus-api-client.js";
import { waitFor } from "../helpers/wait-for.js";

const endpoint = "DataPlatformEndpoint";
const tableName = "DataPlatformInbox";
const appHost = fileURLToPath(new URL("../../CrmErpDemo.AppHost/CrmErpDemo.AppHost.csproj", import.meta.url));

function aspire(...args: string[]): string {
  return execFileSync(process.platform === "win32" ? "aspire.exe" : "aspire",
    [...args, "--apphost", appHost, "--non-interactive"], { encoding: "utf8", timeout: 30_000, windowsHide: true });
}

function inboxTable(): TableClient {
  // Aspire assigns the Functions host's Azurite ports per run.
  const model: { resources: { urls: { name: string; url: string }[] }[] } = JSON.parse(aspire("describe", "--format", "Json"));
  const address = model.resources.flatMap(r => r.urls ?? []).find(u => u.name === "table");
  if (!address) throw new Error("The Functions host storage table endpoint is missing from the Aspire model.");
  const url = new URL(address.url);
  if (!["localhost", "127.0.0.1", "[::1]"].includes(url.hostname)) throw new Error("This showcase expects local Azurite.");
  // Public Azurite development credential, scoped to the loopback emulator above.
  const credential = new AzureNamedKeyCredential("devstoreaccount1",
    "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==");
  return new TableClient(`http://${url.host}/devstoreaccount1`, tableName, credential, { allowInsecureConnection: true });
}

// Same length-prefixed encoding as InboxIdentity.ComputeHash.
function identity(messageId: string): string {
  return createHash("sha256").update([endpoint.length, endpoint, messageId].join("\u001f")).digest("hex").toUpperCase();
}

test("DataPlatform handles two identical ErpCustomerCreated deliveries once", async ({}, testInfo) => {
  const id = randomUUID();
  const erp = await E2eControl.create("erp");
  const nimbus = await NimBusApiClient.create();
  try {
    await erp.sendDataPlatformCustomer(id);
    await waitFor(async () => (await nimbus.history(endpoint, id)).some(m => m.messageType.toLowerCase() === "resolutionresponse"),
      { timeoutMs: 120_000, description: "Resolver records the successful DataPlatform handler" });
    await erp.sendDataPlatformCustomer(id);
    const responses = await waitFor(async () => {
      const history = (await nimbus.history(endpoint, id)).filter(m =>
        m.parentMessageId === id && ["resolutionresponse", "skipresponse"].includes(m.messageType.toLowerCase()));
      return history.length >= 2 ? history : null;
    }, { timeoutMs: 120_000, description: "Resolver records both deliveries" });
    expect(responses.map(m => m.messageType.toLowerCase()).sort()).toEqual(["resolutionresponse", "skipresponse"]);
    expect(responses.find(m => m.messageType.toLowerCase() === "skipresponse")?.errorContent).toMatchObject({ errorText: "DuplicateDetected" });

    expect(await nimbus.searchEvents(endpoint, { eventId: id, resolutionStatus: ["Completed"] })).toHaveLength(1);

    const handlerLogs = await waitFor(() => {
      const logs: { message: string }[] = JSON.parse(aspire("otel", "logs", "dataplatform-adapter", "--search", id, "--limit", "50", "--format", "Json"));
      const matches = logs.map(log => log.message).filter(msg => msg?.includes("DataPlatform received ErpCustomerCreated") && msg.includes(id));
      return Promise.resolve(matches.length > 0 ? matches : null);
    }, { description: "DataPlatform handler log reaches Aspire" });
    expect(handlerLogs).toHaveLength(1);

    const receipt = "R" + identity(id);
    const rowKeys: string[] = [];
    for await (const row of inboxTable().listEntities({ queryOptions: {
      filter: `PartitionKey eq '${identity("")}'`, select: ["RowKey"],
    } })) {
      if (row.rowKey?.endsWith(receipt)) rowKeys.push(row.rowKey);
    }
    expect(rowKeys.sort()).toEqual([expect.stringMatching(new RegExp(`^E[0-9]{19}-${receipt}$`)), receipt]);
    const evidence = { endpoint, messageId: id, handlerLogs, responses, tableName, partitionKey: identity(""), rowKeys };
    await testInfo.attach("dataplatform-inbox", { contentType: "application/json", body: JSON.stringify(evidence, null, 2) });
    console.log(`DataPlatform ${id}: 1 handler invocation, 1 DuplicateDetected, receipt ${receipt} and its time index in ${tableName}.`);
  } finally {
    await Promise.all([erp.dispose(), nimbus.dispose()]);
  }
});
