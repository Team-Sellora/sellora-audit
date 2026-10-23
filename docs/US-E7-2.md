# US-E7-2 — Reporting dashboards (CSP-252)

## Projections (BE1)
Built from the existing five-topic audit consumer, inside the same transaction as each append, so a figure never exists without its audit entry. No extra Kafka consumer. Schema `reporting` (`audit_app`: SELECT, INSERT, UPDATE, DELETE; the `audit` schema stays SELECT, INSERT):

| Table | Key | Filled from |
|---|---|---|
| `order_fact` | company, order | `OrderPlaced`, `OrderApproved`, `OrderConfirmed`, `PaymentRecorded`, `OrderCancelled` |
| `order_fact_line` | company, order, line | the order's `lines` (taken once) |
| `rep_dimension` | company, rep | `SalesRepAssigned`, and reps seen on orders |
| `territory_dimension` | company, territory | `TerritoryAssignedToAgency`, territories seen on orders; names from Organization |
| `agency_dimension` | company, agency | `AgencyRegistered`, agencies seen on orders |

- The status comes from the event type, not the payload, and only moves forward: `Placed` → `Approved` → `Confirmed` (`OrderConfirmed` or `PaymentRecorded`) → `Cancelled`. Times keep their earliest value and snapshot fields only fill gaps, so replayed or out-of-order events give the same row.
- A projection data error (bad value, numeric overflow) never loses the audit entry: the entry is recorded without projections and `ReportingProjectionSkipped` is logged. A rebuild skips the same entry and logs it again.
- Migration `20261014090000_ReportingProjections` (hand-written SQL in `SchemaSql.cs`); the model snapshot matches it.

## What counts as a sale (BE2)
- A sale is an order in status **Confirmed**, counted on its **SaleDate**: the Asia/Colombo date of `confirmedAt` (fixed UTC+05:30, no daylight saving). 23:30 Sri Lanka time on 30 September (18:00 UTC) is a September sale.
- A cancelled order leaves every sales figure and shows under `Cancelled` in the status breakdown.
- **Sales value / expected** = sum of `total`. **Collected** = sum of `paidAmount` (`PaymentRecorded.payment.amount`). **Outstanding** = expected − collected. **AOV** = sales value ÷ order count, 2 dp, half away from zero.
- **Status breakdown**: orders by current status, for orders **placed** in the range (Colombo date of `orderDate`).
- **Daily**: one point per day of the range, zero where nothing sold.
- **Comparison**: `compareFrom`/`compareTo` give the same figures and deltas (absolute, and percent to 1 dp; `null` when the comparison value is 0).
- **Breakdown** by rep, territory, agency or product. Every rep, territory and agency known in scope is listed, at 0 when it sold nothing. Product rows are products that sold.

## API (BE3–BE6)
| Route | Roles |
|---|---|
| `GET /api/reports/sales-summary?from=yyyy-MM-dd&to=yyyy-MM-dd[&compareFrom&compareTo]` | CompanyAdmin, AreaManager |
| `GET /api/reports/sales-breakdown?groupBy=rep\|territory\|agency\|product&from&to[&sort=value\|count\|name&direction=asc\|desc]` | CompanyAdmin, AreaManager |
| `POST /api/reports/projections/rebuild` | CompanyAdmin |

- Both ends of a range are inclusive and a range covers at most 1,100 days; `from > to`, a longer range, a bad `groupBy`, `sort` or `direction` → 400. The company always comes from the token.
- Area Managers get only their provinces (orders and dimensions filtered by `province_id`), resolved from Organization as for the audit log: Organization down → 503. Other roles → 403.
- Responses are cached in process for `Reporting:CacheSeconds` (60) per company, scope and query. No Redis. A rebuild clears that company's entries.
- Territory names: on an interactive request the service asks Organization (`GET /api/hierarchy`) with the caller's token and stores the names on the dimension. If Organization is down the breakdown still answers with short IDs (`Territory 1a2b3c4d`).
- Rebuild deletes the company's projections and replays its audit log in chain order under the append lock, then returns `{ entriesReplayed, ordersProjected, completedAt }`. Figures before and after match (`ReportingTests`).

## DevOps
Run the migrations and re-run `scripts/create-db-roles.sql` (it now creates the `reporting` schema and its grants). Re-import the Audit API in APIM after these routes land. Reporting uses in-process caching; no Redis is deployed.
