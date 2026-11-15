# 0003: PostgreSQL work queue with leases, SKIP LOCKED and fencing tokens

- Status: accepted
- Date: 2026-11-13

## Context

What the matcher cannot decide (about 13-15 % of items on the synthetic data) has to reach a person, once, with the
candidates the matcher saw. Several clerks work the queue at the same time; a clerk can close the browser mid-item;
a reconciliation run can match an item while a clerk is looking at it.

## Options considered

1. **Pessimistic assignment** (`assigned_to` column set by a dispatcher). A clerk who leaves keeps items forever
   unless someone notices.
2. **A message broker** (RabbitMQ, SQS) with visibility timeouts. Correct semantics, but the queue state then lives
   apart from the bank lines and ledger items it refers to, and resolving an item (booking a match) must change both
   atomically.
3. **The queue as a PostgreSQL table** next to the data, claimed with `FOR UPDATE SKIP LOCKED`, with a lease and a
   fencing token.

## Decision

Option 3.

- **Claim** is one statement: `UPDATE ... WHERE id = (SELECT id ... WHERE open OR lease expired ORDER BY priority
  DESC, id FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING ...`. Workers never wait for each other and never get the same
  item; highest amount first.
- A claim is a **lease** (`lease_expires_at`, default 5 minutes, heartbeat to extend). An expired lease makes the item
  claimable again; nothing has to sweep.
- Every claim increments **`lease_token`**. Heartbeat, release and resolve require the caller's token. A clerk whose
  lease expired and was re-claimed gets `409` instead of overwriting the new holder's work: the lease cannot tell a
  dead worker from a slow one, the token makes that harmless. A token that is still current is accepted even after
  expiry, because nobody else wanted the item.
- **Resolve** books a manual match (within a manual tolerance) or a write-off with a mandatory note, in one
  transaction that locks bank lines, then ledger items, then work items, in ascending id order. Reconciliation runs
  lock in the same order, so the two cannot deadlock. A manual match also resolves the work items of its
  counterparts; their holders get `409` on their next call.
- The `UNIQUE` constraints on `match_bank_line.bank_line_id` and `match_ledger_item.ledger_item_id` are the last line
  of defence: two clerks booking the same ledger item from different queue items cannot both succeed.
- Runs upsert work items against a partial unique index (one unresolved item per subject): a re-run refreshes reason
  and candidates but keeps the claim and the attempt count.

## Consequences

- Delivery is at-least-once in the queue sense (an abandoned item is shown again after its lease), effects are
  exactly-once because of the fencing token and the unique constraints. The crash simulation in
  [benchmarks](../benchmarks.md) resolves every item exactly once with 10 % of claims abandoned.
- Throughput is bounded by PostgreSQL round trips, which is far above what people resolve by hand; the measured
  drain rate is reported for completeness.
- The worker id is asserted by the client. With the demo's shared API key anyone holding the key can act as any
  worker; a deployment takes the id from the identity provider's token (threat model).

## Sources

- PostgreSQL `SKIP LOCKED` work-queue pattern and lease/visibility-timeout semantics as used by PGMQ; the "lease
  cannot tell dead from slow, use a fencing token on final writes" argument from the distributed-locking literature
  (Kleppmann, "How to do distributed locking").
