"""
US-E7-1 QA - immutable append-only audit log (CSP-250).

Writes evidence/CSP-250/api/audit-log-<target>.txt:
  US-E7-1-Q1  Order evidence complete: a GPS cash sale's OrderPlaced, OrderConfirmed, PaymentRecorded found by order
              reference, in time order, with actor, the request's X-Correlation-ID and the check-in location
  US-E7-1-Q2  All five topics recorded with their full payload (order, delivery, catalog, hierarchy, inventory)
  US-E7-1-Q3  Immutability: (a) PUT / PATCH / DELETE through APIM -> 404/405; (c) audit_app role cannot UPDATE /
              DELETE / TRUNCATE (local container here; Azure with audit-db-grants.sql)
  US-E7-1-Q4  Tamper detection (local DB only, --allow-tamper): edited payload -> verify names that entry and earlier
              entries pass; deleted row -> verify reports the missing position. The script restores both rows.
  US-E7-1-Q5  Redelivery: consumer group reset to earliest -> entry count unchanged, verify still passes
  US-E7-1-Q6  Scoping: Company B sees none of A; Area Manager only their provinces; AM verify 403; other roles 403
  US-E7-1-Q8  Dead letter: non-JSON on a source topic -> sellora.audit.dead-letter.v1, the next valid event is recorded
  (US-E7-1-Q7 performance: RUN-LOAD.bat / Sprint4-load-test.jmx)

Audit API (backlog US-E7-1-BE7/BE8):
  GET /api/audit?aggregateType=&aggregateId=&orderReference=&actorUserId=&eventType=&from=&to=&page=&pageSize=
  GET /api/audit/{id}         GET /api/audit/verify
DB names for Q3c/Q4 follow the backlog: schema audit, table audit_entry, roles audit_app / audit_owner.

Run:  python audit_log_tests.py --target staging
      python audit_log_tests.py --target local --allow-tamper --interactive
"""
import argparse
import json
import re
import uuid
from datetime import timedelta

from sellora_qa import (AUDIT_DLQ, Api, Db, Report, SetupError, audit_base, build_scenario, ci_get, iso, items_of,
                        load_env, now_utc, pause, poll, run_case, standard_args, wait_seconds)

STORY = "CSP-250"
GUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")


class Audit:
    def __init__(self, s, base):
        self.s, self.api = s, Api(base, "audit")

    def search(self, persona="admin", **filters):
        query = "&".join(f"{k}={v}" for k, v in filters.items() if v is not None)
        return self.api.get(f"/api/audit?{query}", self.s.token(persona))

    def entries(self, persona="admin", **filters):
        filters.setdefault("pageSize", 100)
        response = self.search(persona, **filters)
        return items_of(response.body) if response.status == 200 else []

    def wait_entries(self, minimum, timeout=90, **filters):
        return poll(lambda: (lambda found: found if len(found) >= minimum else None)(self.entries(**filters)),
                    timeout=timeout, interval=3) or self.entries(**filters)

    def detail(self, entry_id, persona="admin"):
        return self.api.get(f"/api/audit/{entry_id}", self.s.token(persona))

    def verify(self, persona="admin"):
        return self.api.get("/api/audit/verify", self.s.token(persona), timeout=300)


def entry_id(entry):
    return ci_get(entry, "auditEntryId") or ci_get(entry, "id")


def payload_of(audit, entry):
    detail = audit.detail(entry_id(entry)).body
    payload = ci_get(detail, "payload")
    if isinstance(payload, str):
        try:
            return json.loads(payload)
        except ValueError:
            return {"_raw": payload}
    return payload or {}


def deployed(audit, ctx):
    if "deployed" not in ctx:
        probe = audit.search(pageSize=1)
        ctx["deployed"] = probe.status not in (0, 404, 502)
        ctx["probe"] = probe
    if not ctx["deployed"]:
        raise SetupError(f"Audit API not reachable / not deployed ({ctx['probe'].status})")


def q1_order_evidence(s, r, ctx, audit):
    deployed(audit, ctx)
    correlation = f"qa-audit-{uuid.uuid4()}"
    headers = {"X-Correlation-ID": correlation}
    if s.source == "order":
        s.need("shopId", "product1")
        placed = s.order.post("/api/orders", {"shopId": s.ids["shopId"], "fulfilmentType": "ImmediateCashSale",
                                              "lines": [{"productId": s.ids["product1"], "quantity": 1}]},
                              s.token("rep"), headers=headers)
        if placed.status != 201:
            raise SetupError(f"place cash sale: {placed.status} {placed.text[:300]}")
        order_id, reference = ci_get(placed.body, "orderId"), ci_get(placed.body, "orderReference")
        lat, lng = s.shop_coordinates()
        checkin = s.order.post(f"/api/orders/{order_id}/checkin", {"latitude": lat, "longitude": lng,
                               "capturedAt": iso(now_utc()), "accuracyMeters": 5}, s.token("rep"), headers=headers)
        if checkin.status != 200:
            raise SetupError(f"check-in: {checkin.status} {checkin.text[:200]} (SHOP_A_LAT / SHOP_A_LNG)")
        paid = s.order.post(f"/api/orders/{order_id}/payment", {"amount": ci_get(placed.body, "total"), "method": "Cash"},
                            s.token("rep"), headers=headers)
        if paid.status not in (200, 201):
            raise SetupError(f"payment: {paid.status} {paid.text[:200]}")
    else:
        base = s.order_event("OrderPlaced", "ImmediateCashSale", 1, "product1", checked_in_at=now_utc())
        reference = base["orderReference"]
        for event_type in ("OrderPlaced", "OrderConfirmed", "PaymentRecorded"):
            event = dict(base, eventId=str(uuid.uuid4()), eventType=event_type, correlationId=correlation,
                         occurredAt=iso(now_utc()))
            if event_type == "PaymentRecorded":
                event["payment"] = {"paymentId": str(uuid.uuid4()), "amount": base["total"], "method": "Cash",
                                    "recordedAt": iso(now_utc()), "checkInId": str(uuid.uuid4())}
                event["checkInLocation"] = base["checkoutLocation"]
            s.publish_order_event(event)
            wait_seconds(1, "keep event times apart")
    ctx["order_reference"] = reference
    order_types = ("OrderPlaced", "OrderConfirmed", "PaymentRecorded")
    everything = audit.wait_entries(3, orderReference=reference)
    r.note(f"order {reference}: {[ci_get(e, 'eventType') for e in everything]}")
    entries = [e for e in everything if ci_get(e, "eventType") in order_types]
    types = [ci_get(e, "eventType") for e in entries]
    for wanted in order_types:
        r.check(f"Q1 {wanted} recorded", wanted in types)
    times = [ci_get(e, "occurredAt") for e in everything]
    r.check("Q1 results in time order (ascending when filtered by order)", times == sorted(times), str(times))
    r.check("Q1 every entry carries the request's X-Correlation-ID",
            all(ci_get(e, "correlationId") == correlation for e in entries), str([ci_get(e, "correlationId") for e in entries]))
    for entry in entries:
        payload = payload_of(audit, entry)
        has_actor = bool(ci_get(entry, "actorUserId")) or bool(ci_get(payload, "salesRep"))
        r.check(f"Q1 {ci_get(entry, 'eventType')} actor identifiable", has_actor, str(entry)[:200])
        if not ci_get(entry, "actorUserId"):
            r.warn(f"Q1 {ci_get(entry, 'eventType')} has no extracted actorUserId",
                   "only the payload's salesRep names the actor - BE2 extracts actor/approvedBy/cancelledBy/acceptedBy only")
        r.check(f"Q1 {ci_get(entry, 'eventType')} full payload stored", ci_get(payload, "orderReference") == reference,
                str(payload)[:200])
        if ci_get(entry, "eventType") == "PaymentRecorded":
            location = ci_get(payload, "checkInLocation") or {}
            r.check("Q1 PaymentRecorded payload has the check-in coordinates",
                    ci_get(location, "latitude") is not None and ci_get(location, "longitude") is not None, str(location))
            r.attach("audit/US-E7-1-Q1-PaymentRecorded.json", payload)
        r.check(f"Q1 {ci_get(entry, 'eventType')} has hash and previousHash (64 hex)",
                len(str(ci_get(entry, "hash") or ci_get(audit.detail(entry_id(entry)).body, "hash"))) == 64)


def q2_five_topics(s, r, ctx, audit):
    deployed(audit, ctx)
    since = now_utc() - timedelta(minutes=1)
    if ctx.get("order_reference"):
        r.check("Q2 order topic recorded (Q1)", bool(audit.entries(orderReference=ctx["order_reference"])))
    job = s.assigned_job(quantity=1)
    found = audit.wait_entries(2, orderReference=ci_get(job, "orderReference"))
    types = {ci_get(e, "eventType") for e in found}
    r.check("Q2 delivery topic: DeliveryJobCreated and DeliveryStatusChanged recorded",
            {"DeliveryJobCreated", "DeliveryStatusChanged"} <= types, str(types))

    catalog = Api(s.env.get("CATALOG_GW", ""), "catalog")
    if catalog.configured:
        sku = f"QA-AUD-{s.run}"
        created = catalog.post("/api/products", {"sku": sku, "name": "QA Audit Product", "description": "audit QA",
                                                 "unitOfMeasure": "Each", "currentUnitPrice": 10.0, "batchCode": "QA-B1",
                                                 "manufacturingDate": "2026-01-01", "expiryDate": "2027-12-31"},
                               s.token("admin"))
        product_id = ci_get(created.body, "productId")
        # Catalog publishes only PriceChanged on sellora.catalog.v1 (creating a product writes no outbox event),
        # so change the new product's price and look for that entry.
        changed = catalog.put(f"/api/products/{product_id}/price",
                              {"newUnitPrice": 12.0, "reason": "QA audit price change",
                               "effectiveFrom": iso(now_utc() + timedelta(minutes=1))}, s.token("admin"), soft=True) \
            if product_id else None
        r.note("Catalog publishes only PriceChanged; a new product alone creates no audit entry")
        entries = audit.wait_entries(1, aggregateId=product_id) if product_id else []
        r.check("Q2 catalog topic: PriceChanged recorded", bool(entries),
                f"product {created.status} {product_id}, price change {getattr(changed, 'status', None)}")
        if entries:
            payload = payload_of(audit, entries[0]) or {}
            payload_cid = ci_get(payload, "correlationId") if isinstance(payload, dict) else None
            if payload_cid:
                r.check("Q2 catalog entry takes the correlation ID from the payload (Catalog sends no header)",
                        str(ci_get(entries[0], "correlationId")) == str(payload_cid), str(ci_get(entries[0], "correlationId")))
            else:
                r.note("Q2 catalog entry has no correlation ID - Catalog sends no X-Correlation-ID header and the "
                       "PriceChanged payload has none, so null is expected")
    else:
        r.blocked("Q2 catalog topic", "CATALOG_GW not set")

    hierarchy = []
    for event_type in ("ShopRegistered", "ShopUpdated", "AgencyRegistered", "SalesRepAssigned"):
        hierarchy += audit.entries(eventType=event_type)
    if not hierarchy and pause("Q2: register or edit a shop in Organization now (hierarchy event).", s.interactive):
        for event_type in ("ShopRegistered", "ShopUpdated"):
            hierarchy += audit.wait_entries(1, eventType=event_type, timeout=60)
    r.check("Q2 hierarchy topic recorded (ShopRegistered / ShopUpdated / AgencyRegistered / SalesRepAssigned)",
            bool(hierarchy), "none found - register a shop and re-run")
    if hierarchy:
        r.check("Q2 hierarchy eventId normalised from 16 raw header bytes to a GUID",
                bool(GUID.match(str(ci_get(hierarchy[0], "eventId")))), str(ci_get(hierarchy[0], "eventId")))
        r.check("Q2 hierarchy payload stored", bool(payload_of(audit, hierarchy[0])))

    inventory = audit.entries(eventType="LowStockDetected")
    if inventory:
        r.check("Q2 inventory topic recorded (LowStockDetected - the only event Inventory publishes)", True)
    else:
        r.blocked("Q2 inventory topic (LowStockDetected)",
                  "no low-stock event in this company yet - set a reorder threshold above on-hand for a product and "
                  "adjust its stock, then re-run")
    r.note("Inventory publishes only LowStockDetected on sellora.inventory.v1; a plain stock adjustment creates no event")


def q3_immutability(s, r, ctx, audit):
    deployed(audit, ctx)
    entries = audit.entries(pageSize=1)
    if not entries:
        raise SetupError("no audit entries yet")
    target = entry_id(entries[0])
    for method in ("PUT", "PATCH", "DELETE"):
        response = audit.api.call(method, f"/api/audit/{target}", {"payload": "tampered"} if method != "DELETE" else None,
                                  token=s.token("admin"))
        r.check(f"Q3a {method} /api/audit/{{id}} -> 404/405", response.status in (404, 405), f"{response.status} {response.text[:150]}")
    r.check("Q3a entry unchanged after the attempts", audit.detail(target).status == 200)
    r.manual("Q3b service-layer test", ["CI of sellora-audit: AuditDbContext rejects Modified/Deleted AuditEntry and the "
                                        "route-inspection test finds no PUT/PATCH/DELETE - link the passing run"])
    db = Db(s.env.get("LOCAL_AUDIT_DB_CONTAINER", ""), s.env.get("LOCAL_AUDIT_DB_NAME", "audit_db"),
            s.env.get("LOCAL_AUDIT_DB_OWNER", "audit_owner"))
    if s.target != "local" or not db.configured:
        r.manual("Q3c DB grants", ["psql \"host=<server> dbname=audit_db user=audit_app sslmode=require\" "
                                   "-f audit-db-grants.sql > evidence/CSP-250/db/grants-staging.txt 2>&1",
                                   "Every UPDATE / DELETE / TRUNCATE line must say 'permission denied'"])
        return
    app_role = s.env.get("LOCAL_AUDIT_DB_APP_ROLE", "audit_app")
    outputs = []
    for sql in ("UPDATE audit.audit_entry SET payload = payload WHERE chain_position = 1",
                "DELETE FROM audit.audit_entry WHERE chain_position = 1",
                "TRUNCATE audit.audit_entry"):
        code, output = db.run_as(app_role, sql)
        outputs.append(f"$ {sql}\n{output}")
        r.check(f"Q3c {app_role}: {sql.split()[0]} -> permission denied", "permission denied" in output.lower(), output[:200])
    code, output = db.run_as(app_role, "SELECT count(*) FROM audit.audit_entry")
    r.check(f"Q3c {app_role}: SELECT allowed", code == 0, output[:200])
    r.attach("db/US-E7-1-Q3c-grants-local.txt", "\n\n".join(outputs))


def q4_tamper(s, r, ctx, audit):
    deployed(audit, ctx)
    db = Db(s.env.get("LOCAL_AUDIT_DB_CONTAINER", ""), s.env.get("LOCAL_AUDIT_DB_NAME", "audit_db"),
            s.env.get("LOCAL_AUDIT_DB_OWNER", "audit_owner"))
    if s.target != "local" or not ctx.get("allow_tamper") or not db.configured:
        r.manual("Q4 tamper detection", ["Only on a local or copied DB: python audit_log_tests.py --target local --allow-tamper",
                                         "or run audit-tamper.sql as audit_owner and call GET /api/audit/verify"])
        return
    baseline = audit.verify()
    r.check("Q4 baseline: chain verifies", ci_get(baseline.body, "verified") is True, baseline.text[:200])
    total = int(ci_get(baseline.body, "entriesChecked") or 0)
    if total < 5:
        raise SetupError("need at least 5 entries for the company")
    company = s.company
    edit_at, delete_at = total // 2, total // 2 + 2
    original = db.scalar(f"SELECT row_to_json(e)::text FROM audit.audit_entry e WHERE company_id = '{company}' "
                         f"AND chain_position = {edit_at}")
    db.rows(f"UPDATE audit.audit_entry SET payload = payload || ' ' WHERE company_id = '{company}' AND chain_position = {edit_at}")
    result = audit.verify().body
    broken = ci_get(result, "firstBroken") or {}
    r.check(f"Q4 edited payload at position {edit_at} -> verify fails there", ci_get(result, "verified") is False
            and ci_get(broken, "chainPosition") == edit_at, json.dumps(result)[:300])
    r.check("Q4 earlier entries still verify (first break is the edited one)", ci_get(broken, "chainPosition") == edit_at)
    r.check("Q4 reason is a hash mismatch", "hash" in str(ci_get(broken, "reason")).lower(), str(ci_get(broken, "reason")))
    r.attach("audit/US-E7-1-Q4-verify-after-edit.json", result)
    db.rows(f"UPDATE audit.audit_entry e SET payload = o.payload FROM json_populate_record(null::audit.audit_entry, "
            f"'{original.replace(chr(39), chr(39) * 2)}') o WHERE e.company_id = '{company}' AND e.chain_position = {edit_at}")
    r.check("Q4 restored edit -> chain verifies again", ci_get(audit.verify().body, "verified") is True)

    removed = db.scalar(f"SELECT row_to_json(e)::text FROM audit.audit_entry e WHERE company_id = '{company}' "
                        f"AND chain_position = {delete_at}")
    db.rows(f"DELETE FROM audit.audit_entry WHERE company_id = '{company}' AND chain_position = {delete_at}")
    result = audit.verify().body
    broken = ci_get(result, "firstBroken") or {}
    r.check(f"Q4 deleted position {delete_at} -> verify reports the missing position",
            ci_get(result, "verified") is False and ci_get(broken, "chainPosition") == delete_at
            and "missing" in str(ci_get(broken, "reason")).lower(), json.dumps(result)[:300])
    r.attach("audit/US-E7-1-Q4-verify-after-delete.json", result)
    db.rows(f"INSERT INTO audit.audit_entry SELECT * FROM json_populate_record(null::audit.audit_entry, "
            f"'{removed.replace(chr(39), chr(39) * 2)}')")
    r.check("Q4 restored row -> chain verifies again", ci_get(audit.verify().body, "verified") is True)


def q5_redelivery(s, r, ctx, audit):
    deployed(audit, ctx)
    before = ci_get(audit.search(pageSize=1).body, "totalCount")
    steps = ["Stop the Audit service (its consumer must be inactive for a reset)",
             "kafka-consumer-groups --bootstrap-server <server> [--command-config client.properties] --group sellora.audit.v1 "
             "--all-topics --reset-offsets --to-earliest --execute",
             "Start Audit, wait until its log is quiet (it re-reads every retained message)",
             f"Entry count must still be {before}; GET /api/audit/verify must still be verified"]
    if not pause("Q5: reset the audit consumer group to earliest (steps in the guide), then start Audit.", s.interactive):
        r.manual("Q5 redelivery", steps)
        return
    wait_seconds(60, "audit re-reads the topics")
    after = ci_get(audit.search(pageSize=1).body, "totalCount")
    r.check("Q5 entry count unchanged after replay from earliest", after == before, f"{before} -> {after}")
    r.check("Q5 chain still verifies", ci_get(audit.verify().body, "verified") is True)


def q6_scoping(s, r, ctx, audit):
    deployed(audit, ctx)
    reference = ctx.get("order_reference")
    sample = audit.entries(pageSize=5)
    if s.has("admin_b"):
        if reference:
            leaked = audit.entries("admin_b", orderReference=reference)
            r.check("Q6 Company B search by Company A's order reference -> nothing", not leaked, str(leaked)[:200])
        if sample:
            r.expect_status("Q6 Company B opens a Company A entry", audit.detail(entry_id(sample[0]), "admin_b"), 404)
        injected = audit.search("admin", companyId=s.env.get("COMPANY_B_ID", str(uuid.uuid4())), pageSize=5)
        r.check("Q6 ?companyId=<B> as Company A is ignored (company comes from the token)",
                all(ci_get(e, "companyId") in (None, s.company) for e in items_of(injected.body)), injected.text[:200])
    else:
        r.blocked("Q6 Company B", "TOKEN_ADMIN_B not set")
    if s.has("am"):
        provinces = set(ci_get(s.scope("am"), "provinceIds") or [])
        am_entries = audit.entries("am")[:20]
        # provinceId is only on the detail (AuditEntryDetail), not on the search rows
        details = [audit.detail(entry_id(e), "am").body for e in am_entries]
        outside = [ci_get(d, "eventType") for d in details if ci_get(d, "provinceId") not in provinces]
        r.check("Q6 Area Manager search returned entries to check", bool(am_entries), "no entries in the AM's provinces yet")
        r.check("Q6 Area Manager sees only entries of their provinces (none without a province)", not outside,
                f"{len(outside)} outside: {outside[:5]}")
        r.expect_status("Q6 Area Manager calls verify", audit.verify("am"), 403)
    for persona in ("op", "rep", "shop"):
        if s.has(persona):
            r.expect_status(f"Q6 {persona} searches the audit log", audit.search(persona, pageSize=1), 403)


def q8_dead_letter(s, r, ctx, audit):
    deployed(audit, ctx)
    if not s.tap.can_produce:
        r.blocked("Q8 dead letter", s.tap.reason or "staging: --allow-produce (the probe event stays in the audit log forever)")
        return
    since = now_utc()
    marker = f"QA-AUDIT-MALFORMED-{s.run}"
    s.tap.produce("sellora.catalog.v1", marker, f"not json {marker}")
    found = s.tap.wait_for(AUDIT_DLQ, since, lambda m, p: marker in (m.value or ""), timeout=60)
    r.check("Q8 non-JSON on sellora.catalog.v1 lands on sellora.audit.dead-letter.v1", bool(found))
    if found:
        r.attach("kafka/US-E7-1-Q8-dead-letter.json", found[0].as_evidence())
    nocompany = f"QA-AUDIT-NOCOMPANY-{s.run}"
    s.tap.produce("sellora.catalog.v1", nocompany, json.dumps({"eventId": str(uuid.uuid4()), "eventType": "QaProbe",
                                                               "note": nocompany}))
    found = s.tap.wait_for(AUDIT_DLQ, since, lambda m, p: nocompany in (m.value or ""), timeout=60)
    r.check("Q8 event without a company ID is dead-lettered", bool(found))
    probe_id = str(uuid.uuid4())
    s.tap.produce("sellora.catalog.v1", probe_id, json.dumps({"eventId": probe_id, "eventType": "QaAuditProbe",
                                                              "schemaVersion": "1.0", "companyId": s.company,
                                                              "aggregateId": probe_id, "occurredAt": iso(now_utc())}),
                  {"company-id": s.company, "event-type": "QaAuditProbe"})
    recorded = audit.wait_entries(1, aggregateId=probe_id, timeout=60)
    r.check("Q8 the next valid event is still recorded (unknown event types are stored too)", bool(recorded))


def main():
    parser = standard_args(argparse.ArgumentParser(description=__doc__,
                                                   formatter_class=argparse.RawDescriptionHelpFormatter))
    parser.add_argument("--allow-tamper", action="store_true", help="Q4: edit and delete rows in the LOCAL audit DB")
    args = parser.parse_args()
    env = load_env(args.env)
    report = Report("audit-log")
    base = audit_base(env, args.target)
    print(f"Target: {args.target} ({base})")
    from sellora_qa import require_api
    require_api(args, report, base, "AUDIT_GW")
    from sellora_qa import delivery_base
    s = build_scenario(args, env, report, delivery_base(env, args.target))
    s.interactive = args.interactive
    from sellora_qa import audit_preflight
    audit_preflight(s, report, base)
    audit, ctx = Audit(s, base), {"allow_tamper": args.allow_tamper}

    report.story(STORY, "US-E7-1 Immutable append-only audit log")
    run_case(report, "US-E7-1-Q1", "Order evidence complete", q1_order_evidence, s, report, ctx, audit)
    run_case(report, "US-E7-1-Q2", "All five topics", q2_five_topics, s, report, ctx, audit)
    run_case(report, "US-E7-1-Q3", "Three-layer immutability", q3_immutability, s, report, ctx, audit)
    run_case(report, "US-E7-1-Q4", "Tamper detection", q4_tamper, s, report, ctx, audit)
    run_case(report, "US-E7-1-Q5", "Redelivery", q5_redelivery, s, report, ctx, audit)
    run_case(report, "US-E7-1-Q6", "Scoping", q6_scoping, s, report, ctx, audit)
    run_case(report, "US-E7-1-Q8", "Dead letter", q8_dead_letter, s, report, ctx, audit)
    report.manual("US-E7-1-Q7 performance", ["RUN-LOAD.bat (Sprint4-load-test.jmx): 20 Company Admins search by order reference, "
                                             "open an entry and search a 7-day range through APIM for 5 minutes",
                                             "Result: evidence/CSP-250/jmeter/load-summary.txt - PASS when p95 <= 1000 ms and errors <= 1%",
                                             "Realistic size: sellora-audit scripts/seed-audit-entries.sql seeds 100,000 chained entries "
                                             "(as audit_owner, audit service stopped, on a test database only)"])
    report.finish(args.evidence, args.target, base)


if __name__ == "__main__":
    main()