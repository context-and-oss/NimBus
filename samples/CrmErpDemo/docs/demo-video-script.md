# NimBus demo video — shot-by-shot script

Working title: **"The life of one customer account"**
Target length: **~11 minutes**. Five acts, one continuous story.

The premise: rather than touring features, we follow a single business object — an
account called **Acme GmbH** — from creation through failure, recovery, a slow
external system, and synchronous questions. Every NimBus differentiator shows up as
a consequence of the story rather than as a bullet on a slide.

The acts are recorded by the Playwright harness in [`../e2e/demo/`](../e2e/demo/)
(`npm run demo` from `samples/CrmErpDemo/e2e`). Each act produces one `.webm` in
`e2e/demo-footage/`. On-screen captions listed below are burned in by the harness, so
the narration and the caption text are deliberately *not* identical — the caption is
the headline, the voice-over is the explanation.

**The Narration column is the editorial script; the recording script is
[`../e2e/demo/stage/narration.ts`](../e2e/demo/stage/narration.ts)**, keyed by the
shot numbers below. That file holds the same lines in spoken form (`CrmAccountCreated`
becomes "CRM Account Created") and is what `npm run demo:voice` sends to ElevenLabs.
Edit a line here and there, or the film and the script drift apart. Shots marked
*(not recorded)* — and 2.5, whose drill-in the harness doesn't perform yet — have a
line in that file but no cue, so nothing narrates over footage that doesn't show it.

---

## Before you record

| Item | Value |
|---|---|
| Service Bus | **Real Azure namespace**, not the emulator. Emulator 2.0.0 drops AMQP connections during warm-up and produces `MessagingEntityNotFound` noise on camera. |
| Storage provider | SQL Server (default). No Cosmos secret needed. |
| Warm-up | Create and delete one throwaway account before recording so DbUp migrations, Functions cold starts, and first-JIT latency don't appear in Act 1. |
| Ports | `crm-api` 5080, `erp-api` 5090, `nimbus-ops` 18080 are pinned. `crm-web`/`erp-web` get Aspire-assigned ports — the harness discovers them by page title. |
| Reset state | Error mode OFF, service mode OFF, handoff mode OFF before every take. |

---

## Act 1 — The happy path (~2:00)

**Purpose:** establish what the system *is* and that integration works, so the
audience has a baseline to lose in Act 2.

| # | Shot | On-screen caption | Narration |
|---|---|---|---|
| 1.1 | Aspire dashboard, resource list scrolling | *Two systems, one bus* | "This is a CRM and an ERP — separate databases, separate APIs, separate deployment models. The CRM adapter is a worker container; the ERP adapter is an Azure Functions app. Same handler code in both." |
| 1.2 | crm-web → Accounts (empty-ish list) | *CRM: create an account* | "Everything starts the way it does in real life: somebody creates a customer in the CRM." |
| 1.3 | Fill the new-account form: "Acme GmbH", DE, tax ID | — | "Legal name, country, tax ID. Nothing about messaging in this form — the CRM API just saves the row and publishes `CrmAccountCreated`." |
| 1.4 | Accounts list — ERP sync column reads `pending…` | *ERP sync: pending…* | "The account is saved. The ERP sync column says pending — the event is on its way across the bus." |
| 1.5 | Same row flips to `✓ C-…` (list self-polls every 3s) | *ERP customer number, back in the CRM* | "And there it is. The ERP created a customer, published its own event back, and the CRM wrote the ERP customer number onto its own row. That round-trip crossed two topics and two hosting models." |
| 1.6 | erp-web → Customers, the new row with Origin `Crm` | *ERP: same customer, arrived by event* | "Same customer on the ERP side, tagged with the origin it came from." |
| 1.7 | nimbus-ops → ErpEndpoint, both events on one session | *Every message, audited by session* | "And this is what makes NimBus different from raw Service Bus: a full audit trail. Both events, same session key, in order — without anybody writing logging code." |

**Editing note:** 1.4 → 1.5 is the money shot. Don't cut away; let the row flip on
camera. It typically takes a couple of seconds against a real namespace.

---

## Act 2 — Something breaks (~3:00)

**Purpose:** the strongest segment. Lead marketing material with this. Plain
pub/sub gives you a dead-lettered message and a support ticket; NimBus gives you an
operator who fixes it in a browser.

| # | Shot | On-screen caption | Narration |
|---|---|---|---|
| 2.1 | erp-web → flip **Error mode: ON**, amber banner drops in | *Break the ERP on purpose* | "Let's break the ERP. Every inbound handler will now throw — this is the integration equivalent of the downstream system falling over at 2am." |
| 2.2 | crm-web → create "Contoso Logistics" | *CRM keeps working — it doesn't know* | "The CRM doesn't know or care. It saves its row and publishes, exactly as before. That's the point of decoupling." |
| 2.3 | crm-web → update the same account twice | *Two more edits, same account* | "Now two more edits to the same account, hard on the heels of the first event." |
| 2.4 | nimbus-ops → ErpEndpoint list: head row `Failed`, siblings `Deferred` | *Failed head, deferred siblings* | "Here's the behaviour you don't get for free. The first message failed. The two updates behind it did **not** overtake it — they're deferred, parked behind the failure, because they share a session key. Order is preserved through the outage." |
| 2.5 | Click into the failed message — error detail | *The actual exception, kept* | "The operator can see the real exception, the payload, and the delivery history. No log-diving." |
| 2.6 | erp-web → **Error mode: OFF** | *Fix the underlying system* | "We fix the ERP." |
| 2.7 | nimbus-ops → click **Resubmit** on the failed row | *One click: resubmit* | "And the operator resubmits — one click, from a browser, no redeploy and no message surgery." |
| 2.8 | List drains: head `Completed`, both deferred rows replay in order | *The backlog drains in order* | "The head succeeds, the session unblocks, and the parked updates replay in FIFO order automatically. Nobody had to touch the deferred messages." |
| 2.9 | erp-web → customer shows the *latest* name | *Final state is correct* | "The ERP ends up with the most recent version — not whichever message happened to win a race." |

**Editing note:** 2.4 is the conceptual heart of the video. Consider a freeze-frame
with an annotation arrow on the `Deferred` badges.

---

## Act 3 — The slow external system (~2:30)

**Purpose:** the scenario nobody else handles well — the downstream system accepts
work but finishes it minutes later. Models Dynamics/DMF-style imports.

| # | Shot | On-screen caption | Narration |
|---|---|---|---|
| 3.1 | erp-web → Admin: duration slider to **60s**, failure 0%, **Pending-handoff mode ON** | *Hand the work to an external job* | "Real ERPs often don't do the work while you wait. They accept a job and finish it later. NimBus has a first-class state for that." |
| 3.2 | crm-web → create "Northwind Traders" | *Same flow as before* | "New account, nothing special about it." |
| 3.3 | crm-web → two quick edits to the same account | *Two edits, straight behind it* | "And immediately, two edits to the same account — published while the external import is still open." |
| 3.4 | nimbus-ops → **Pending** row + *Awaiting external*; PENDING stat tile | *Pending — work is in flight elsewhere* | "The message isn't failed and it isn't complete. It's pending — the handler said 'an external job has this' and returned. The broker message is settled, but the audit trail knows it isn't finished." |
| 3.5 | Same view → the two edits show **Deferred**; DEFERRED stat tile | *And the siblings wait for it* | "They defer. The session stays ordered across an asynchronous, out-of-process wait — not just across a retry." |
| 3.6 | Message detail: `HandoffReason`, `ExternalJobId`, `ExpectedBy` | *A job ID you can chase* | "It carries the external job id and when we expect it back — so an operator chases the actual import, not just the message." |
| 3.7 | erp-web → In-flight handoff jobs panel counting down | *The external job, ticking* | "Meanwhile the simulated ERP import counts down to settlement." |
| 3.8 | nimbus-ops → all three rows **Completed** | *Settled — and the backlog replays* | "The import finishes, calls back into NimBus, the original message completes, and the deferred edits replay in order. No operator involvement at all." |
| 3.9 | erp-web → customer on the latest name | *Correct final state* | "Through a sixty-second external wait and two concurrent edits." |
| 3.10 | *(not recorded)* failure rate 100% → row **Failed** with `DMF rejected: …` → **Skip** | *When the external job rejects* | "If the external job rejects the work instead, the error text comes back verbatim and the operator can skip it to unblock the session." |

**Why the edits come before the operator shots:** the handoff window is capped at
the slider's 60-second maximum, and the nimbus-ops shots alone burn more than half
of it. Publishing the sibling edits later means the handoff has already settled and
nothing defers — the first take of this act failed exactly that way. The harness
now asserts a `Deferred` sibling exists before it narrates one.

**Editing note:** the duration slider is your pacing tool, but 60s is the maximum
the panel allows. If you need a longer window for a manual take, drive
`PUT /api/admin/handoff-mode` directly.

---

## Act 4 — Beyond fire-and-forget (~2:00)

**Purpose:** kill the objection "so it's just events." Two showcases that prove each
other, with zero setup.

| # | Shot | On-screen caption | Narration |
|---|---|---|---|
| 4.1 | crm-web → open the Acme account → **Run ERP credit check** | *A question, not a notification* | "Some things aren't notifications. 'Can this customer buy on credit right now' is a question, and you need the answer before you respond to the user." |
| 4.2 | Green **Approved · C-…** badge appears, sub-second | *Request/reply, typed, sub-second* | "Request/reply over Service Bus sessions. Typed request, typed answer, back in well under a second — and the request itself is a normal audited NimBus event." |
| 4.3 | **Place credit hold in ERP** → confirm dialog | *A command: imperative, one consumer* | "This one is different again. It's not a notification and it's not a question — it's an instruction. NimBus models that as a Command, and the platform refuses to start if a command has anything other than exactly one consumer." |
| 4.4 | erp-web → Customers: amber **Credit hold** badge | *ERP obeyed — no event came back* | "The ERP applied the hold. Notice nothing was published back — a command doesn't owe you an event." |
| 4.5 | crm-web → **Run ERP credit check** again → amber **On hold** | *The two showcases prove each other* | "And running the credit check again returns 'on hold'. The command and the query just validated each other." |

---

## Act 5 — The ecosystem close (~1:30)

**Purpose:** widen out. NimBus isn't a closed world.

| # | Shot | On-screen caption | Narration |
|---|---|---|---|
| 5.1 | crm-web → Contacts: lead row with violet **Partner** badge | *A partner with zero NimBus code* | "This contact didn't come from the CRM or the ERP. It came from a simulated third-party system that references only the raw Azure Service Bus SDK — no NimBus packages at all." |
| 5.2 | Caption card over the contacts list | *Plain CloudEvents 1.0, in and out* | "It publishes plain CloudEvents. NimBus consumes them natively — synthesising its own routing from the envelope — and publishes its own events back out as CloudEvents that the partner reads without knowing what NimBus is." |
| 5.3 | nimbus-ops → endpoints overview, live counts | *One operator surface over all of it* | "And all of it — two internal systems, an external partner, sync and async — lands in one operator surface with one audit trail." |
| 5.4 | Hold on the endpoints view; fade | *NimBus* | "That's NimBus." |

---

## What was deliberately cut

- **Inbox deduplication** — real and useful, but on camera it's one log line and an
  absent side effect. Save it for a deep-dive video.
- **Notification alerts (webhook → panel)** — same problem: a card appears. Better as
  a screenshot in written material.
- **Cosmos vs SQL storage toggle, emulator mode, provisioning internals** — one
  narrated sentence over the Aspire dashboard in shot 1.1, no dedicated screen time.
- **ERP-originated and contact flows (README flows 2 and 3)** — mechanically identical
  to Act 1 with no new payoff.
- **The `nb` CLI and EventCatalog export** — a different audience (developers
  evaluating), not this video's arc.

## YouTube title and description

Published: **https://youtu.be/jZ99gbYZLqU**. The film is embedded on the GitHub Pages
site only — `site/index.html`, the `#demo-video` section under the hero — deliberately
not in any README. If the video is ever re-uploaded, the id appears in exactly two
places: that section (iframe src plus six chapter deep-links) and this document.

Title (≤60 chars so it isn't truncated in search). This one is the film's own
opening line, so the title and the cold open say the same thing:

> **Moving the message is the easy part — NimBus for Azure**

Alternates, in the order I'd try them:

- `When Integration Breaks at 2am — NimBus on Azure Service Bus` (60)
- `Failed. And nothing overtook it. — NimBus on Service Bus` (56)
- `The life of one customer account — a NimBus demo` (47)

Description — paste as-is. The chapter times are measured from the current cut
(`e2e/demo-film/nimbus-demo.mp4`, ~8:10, joined 2026-08-06) by taking each act's
offset in `nimbus-demo.vtt` minus its offset in its own act `.vtt`. **Re-derive
them the same way after any re-record**, since act durations move with narration
length. YouTube needs the first chapter at `0:00` and every chapter ≥10s.

```text
A CRM and an ERP, two databases, two hosting models, one Azure Service Bus. We follow a
single customer account — Acme GmbH — from creation through a downstream outage, a
sixty-second external import, and two synchronous questions, and let every NimBus
feature show up as a consequence of the story instead of a bullet on a slide.

NimBus is an Azure-native event-driven integration platform: session-based ordered
processing, a centralized Resolver with a full audit trail, and an operator UI where a
human fixes a failed message in a browser instead of filing a support ticket.

Chapters
0:00  What NimBus is, and what follows
1:08  Two systems, one bus — the happy path
2:33  Something breaks: failed head, deferred siblings, one-click resubmit
4:20  The slow external system: PendingHandoff and a job ID you can chase
6:10  Beyond fire-and-forget: request/reply and commands
7:16  A partner with zero NimBus code — and the close

What you'll see
• A CRM→ERP→CRM round-trip across two topics and two hosting models — the CRM adapter is
  a worker container, the ERP adapter an Azure Functions app, running identical handler code.
• Session-based ordering under failure: the head message fails, the two edits behind it
  defer rather than overtake it, and they replay in FIFO order after the operator resubmits.
• Resubmit and skip from a browser — no redeploy, no message surgery, no log-diving.
• PendingHandoff: the handler tells NimBus "an external job has this", the broker message
  settles, the session stays ordered across a sixty-second out-of-process wait, and the
  external job settles the audit row later.
• Request/reply over Service Bus sessions — a typed question with a typed answer, sub-second.
• Commands: exactly one consumer, enforced at provisioning time; the platform refuses to
  start if anyone declares a second.
• CloudEvents 1.0 interop with an external partner that references only the raw Azure
  Service Bus SDK — no NimBus packages at all.

Everything in this video runs from the CrmErpDemo sample in the repo, against a real Azure
Service Bus namespace. Nothing is mocked and nothing is sped up.

Source: https://github.com/akakaule/NimBus
The sample: https://github.com/akakaule/NimBus/tree/master/samples/CrmErpDemo
Packages: https://www.nuget.org/packages?q=Akaule.NimBus

#dotnet #azure #servicebus #eventdriven #integration #microservices #azurefunctions
```

Notes for whoever uploads it:

- The thumbnail should be the 2.4 frame — `Failed` head row with two `Deferred` siblings —
  with the words **"Failed. And nothing overtook it."** That shot is the video's argument.
- Set the video language to English and upload `demo-film/nimbus-demo.vtt` (written by
  `npm run demo:film`) as the subtitle track; don't rely on auto-captions for
  `CrmAccountCreated`-style identifiers — the narration says them as words
  ("CRM Account Created") and auto-captions will spell them that way too.
- "Not made for kids". Category: Science & Technology.

---

## Recording practicalities

- Arrange crm-web, erp-web and nimbus-ops as three browser windows if you re-shoot
  manually; the automated harness uses one window and navigates, which cuts cleanly.
- Do a throwaway account create before the real take (Functions cold start).
- Between takes, reset: error mode OFF, service mode OFF, handoff mode OFF.
- The Playwright specs in `../e2e/tests/` (01, 03, 04, 05, 09, 10) are the regression
  equivalents of Acts 1–4 — if a shot stops working, the matching spec tells you
  whether the demo or the platform broke.

## Possible future act: circuit breaker

The circuit-breaker showcase (README → "Showcase: Circuit breaker") is filmable as its own
act: crm-web's outage toggle flips ON, the panel goes Open ("Receiver paused") within ~20
seconds, nimbus-ops shows no DLQ flood, and after the toggle flips OFF the panel walks
Half-open → Closed inside a minute. Regression equivalent: `../e2e/tests/11-circuit-breaker-recovery.spec.ts`.
Between takes, reset: CRM API outage OFF (crm-web Demo controls pane).
