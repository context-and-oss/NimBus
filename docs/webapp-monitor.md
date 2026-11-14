# The Monitor wall

`/Monitor` is a full-screen overview of the whole platform, meant for a screen on the wall and
read from across a room. It refreshes itself and needs no clicks.

## Who can open it

- **A signed-in person with the site Reader role** (or higher). They see every endpoint. A
  Contributor on an endpoint can also acknowledge its failures. Without the site Reader role the
  page says so; an endpoint-level role is not enough, because every number on the page is
  fleet-wide.
- **A wall display with a dashboard token.** A site Owner issues one per wall in
  **Settings → Dashboard tokens** and opens the link it shows, `/Monitor#token=nbdt_…`, in the
  wall's browser. The wall sees the same page read-only: no ACK, and no access to any other page or
  API. When the token is revoked or expires the wall shows that its link is no longer valid and
  keeps retrying. See [Authentication → Dashboard tokens](authentication.md#dashboard-tokens).

## What it shows

**Header.** Environment, the time since the oldest unacknowledged failure (T+), and the local and
UTC time.

**Summary.**

| Tile | Meaning |
|---|---|
| Health ring | One segment per endpoint, coloured by its state (below); the centre counts the healthy ones |
| Unresolved failures | Messages that need an operator now: failed, dead-lettered and unsupported |
| Blocked sessions | An estimate: sessions with a failed, dead-lettered or pending hand-off message, and the two endpoints with most. A message dead-lettered on its first attempt does not block its session but is counted; sessions only waiting for replays are not. The exact state lives on Service Bus and is too slow to read every few seconds |
| Backlog | Pending plus deferred messages, and how fast that changes per minute |
| Throughput now | Messages published in the last complete minute, with handled and failed |
| Success rate · last hour | Handled ÷ (handled + failed attempts) over the last 60 minutes. Counts attempts: a message that failed twice and then succeeded counts two failures and one success |
| Time to handle · last hour | Average queue wait plus average processing time over the last 60 minutes, each from the messages that recorded it |

**Charts.**

- *Last hour*: published, handled and failed per minute. The last minute is still filling up.
- *Last 3 days*: the same per hour; the current hour is dashed.
- *Time to handle*: average processing time and average queue wait per hour over 24 hours. An hour
  without timed messages leaves a gap. Storage providers that cannot report this show a note
  instead.

**Endpoints.** One row per endpoint, failing first (largest first), then acknowledged, then
backlog (deepest first), then the rest A to Z. The state badge says what is happening and how
much:

| State | Meaning |
|---|---|
| Failing · n | n unresolved messages (failed, dead-lettered or unsupported) that nobody has acknowledged |
| Acked · n | Still failing, but someone has acknowledged it; their name shows beside it |
| Queued · n | Nothing failing; n messages pending or deferred |
| Healthy | Nothing failed and nothing waiting |

In, out and fail % cover the last 60 minutes; pending and deferred are the current state and
break down the queued count. Processing shows the last hour's average
and calls it out when it is 1.5 times its 24-hour average or more. Heartbeat shows when the
endpoint last answered a probe.

**Footer.** Recent failures, recoveries and acknowledgements, and when the page last updated.

## Refresh and outages

The page reads three operations of the [Monitor feed](webapp-rest-api.md#monitorfeed): status
every 5 seconds, the last-hour metrics every 30 seconds and the history every 5 minutes. The
server composes each once per interval for every wall, so adding screens does not add store load.

If the page cannot reach the server for 30 seconds it shows *Connection lost* and greys out. If the
server cannot read the store it keeps serving its last result, marked as such, and tries the store
again once per interval.

## Setting up a wall

1. In **Settings → Dashboard tokens**, issue a token named after the wall, for example
   "Operations wall, 3rd floor". The default expiry is one year.
2. Copy the link from the dialog. It is shown once.
3. Open the link in the wall's browser, full screen, and bookmark it there.
4. When the wall is retired or the link may have leaked, revoke its token. The wall stops within
   30 seconds.

The local Aspire stack signs every request in as an administrator unless it runs with
`NIMBUS_IDENTITY=true`, so try a token-only wall with Identity enabled and a private window.
