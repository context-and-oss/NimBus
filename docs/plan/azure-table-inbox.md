# Azure Table Storage subscriber inbox

Azure adapters with a Storage account can share a pre-provisioned inbox table. Add a standalone provider package, register a caller-supplied primary-endpoint TableClient, and select the provider through UseInbox. This keeps authentication, provisioning, and SDK retries under the adapter's control.

## Storage layout

Use one partition per hashed endpoint for lightweight workloads. Use the existing InboxIdentity hash for each receipt. In the same partition, insert the receipt and a creation-time-ordered index entry in a single entity group transaction. A duplicate insert keeps the original timestamp. A lookup is one point read. Cleanup reads only the endpoint's expired index range, then conditionally deletes each index entry together with its receipt. Bound each pass and propagate storage failures. Only a missing entity counts as a lookup miss.

## Implementation and verification

1. Add live provider tests using the shared inbox conformance suite. Exercise missing tables, special characters, concurrent cleanup and re-recording. Confirm the tests fail before implementing.
2. Add the provider, SDK selection and missing-registration guidance. Document package setup, managed identity, cleanup capacity and the existing record-on-success guarantee.
3. Run live tests against Azurite, add the emulator to CI, build and test in Release, and check package and documentation output.

## DataPlatform inbox showcase in the CRM/ERP demo

Use the existing Functions host storage for a DataPlatformInbox table. Resolve the emulator connection string locally and the primary table service URI with managed identity on Azure. Create the table idempotently before the subscriber starts. Keep CRM's SQL inbox and ERP's application-level idempotence.

Exercise a pair of ErpCustomerCreated deliveries with a unique shared broker identity through the Development-only demo harness. Check the actual handler log, Resolver history and matching receipt and time-index row in Azurite. Include reproducible commands and the expected evidence in the demo guide.

Verify the failing scenario before enabling the inbox, repeat it after wiring, then build in Release and run the relevant regression checks. The live scenario uses the sample's own Service Bus emulator, Functions host, Resolver and storage.
