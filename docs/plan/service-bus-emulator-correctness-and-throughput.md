# Service Bus emulator correctness and throughput

1. Pin the review reproductions with SDK, broker and journal regression tests. Run against the original implementation.
2. Commit a batch as a unit under the namespace lock. Account for stored bodies and session state as they change, including forwarding, scheduled activation, settlement, expiry and deletion. Preserve accepted schedules under temporary capacity pressure.
3. Check deadlines before DLQ settlement and replay. Process lock expiry before returning a delivery. Track the earliest maintenance deadline so idle receivers skip unrelated backlogs.
4. Enforce the configured message limit on the management scheduling path. Validate the whole journal before touching the running broker.
5. Run the emulator Release suite, repeat the isolated backlog measurement and run repository Release checks.
