# sellora.audit.dead-letter.v1

Records the audit consumer could not store. Each is the **original record, unchanged** — same key, same value bytes, all original headers — so it can be inspected and replayed onto its source topic once fixed. Audit adds these headers (UTF-8 text):

| Header | Example | Meaning |
|---|---|---|
| `x-dead-letter-reason` | `NotJson: The message value is not JSON: …` | Rejection reason and detail (at most 1,000 characters) |
| `x-source-topic` | `sellora.order.v1` | Where it came from |
| `x-source-partition` | `2` | |
| `x-source-offset` | `77` | |
| `x-dead-lettered-at` | `2026-10-13T08:15:02.1234567+00:00` | ISO 8601 |

Reasons: `EmptyPayload`, `InvalidUtf8`, `ContainsNulCharacter`, `NotJson`, `NotJsonObject`, `MissingCompanyId`, `CompanyMismatch`, and `PostgreSQL refused the record: …` for data the database rejects.

The produce is acknowledged (`acks=all`, idempotent) before the source offset is committed. Suggested retention: 7–14 days. Producer ACL: the audit service account only.

Replay: fix the producer or the record, then produce the value with its original headers to the `x-source-topic`. Audit de-duplicates on `(companyId, eventId)`, so replaying a record that was in fact stored is harmless.
