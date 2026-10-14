# Producer fixtures

One Kafka record per file, in the exact shape each producer's `dev` branch publishes (topic, key, headers, value text), plus an `expected` block with what the audit entry must contain. `ProducerFixtures` loads them; `EnvelopeNormaliserTests` checks every one, and the Kafka integration test produces all of them to a real broker.

| Folder | Producer | Notes |
|---|---|---|
| `organization/` | sellora-organization outbox | `event-id` header is the 16 raw GUID bytes |
| `catalog/` | sellora-catalog outbox | raw-byte `event-id`, no correlation ID |
| `order/` | sellora-order outbox (`order-events.v1`) | text `event-id`; payment events carry `checkInLocation` |
| `inventory/` | sellora-inventory | no headers, null key, PascalCase payload |
| `delivery/` | sellora-delivery | `x-` headers, no event ID (derived from topic/partition/offset), PascalCase payload |

`header.text` is UTF-8 text; `header.base64` is raw bytes. When a producer changes its envelope, update the fixture and the `expected` block together.
