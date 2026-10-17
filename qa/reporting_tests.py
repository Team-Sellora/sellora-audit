"""
US-E7-2 QA - reporting dashboards (CSP-252).

Writes evidence/CSP-252/api/reporting-<target>.txt and evidence/CSP-252/calc/*.csv (the manual calculation):
  US-E7-2-Q1  Month view figures match a calculation from the order records (BE2 definition: a sale is a confirmed,
              not-cancelled order counted on its ConfirmedAt date in Asia/Colombo; expected = confirmed total,
              collected = PaymentRecorded amounts, outstanding = expected - collected). Local: injects orders
              across three agencies including one confirmed at 23:30 Colombo on the last day of the month
              (18:00 UTC, must stay in that month) and one at 00:30 Colombo on the 1st (19:00 UTC the day
              before, must move to the new month).
  US-E7-2-Q2  Area Manager: no figure includes another province
  US-E7-2-Q3  Comparison period: deltas equal the two periods calculated separately
  US-E7-2-Q4  Rep breakdown: a rep with no sales shows 0; sorting by count and by value both work
  US-E7-2-Q5  Cancellation: a cancelled confirmed order leaves sales and shows under Cancelled
  US-E7-2-Q6  Live and replay: a new order appears within the cache TTL; rebuild gives identical figures
  US-E7-2-Q7  Access: Agency Operator, Sales Rep, Shop Owner 403; an empty range shows zeros / empty
  (route guard and empty state on the page: selenium_sprint4_ui.py --scenario reports)

Report API (backlog US-E7-2-BE3..BE6), on the Audit gateway:
  GET  /api/reports/sales-summary?from=&to=[&compareFrom=&compareTo=]
  GET  /api/reports/sales-breakdown?groupBy=rep|territory|agency|product&from=&to=&sort=
  POST /api/reports/projections/rebuild
Field names follow sellora-audit dev (ReportContracts.cs): totals{salesValue, orderCount, averageOrderValue, expected, collected, outstanding}, statusBreakdown[{status,count}], daily[], comparison{totals, deltas}, breakdown rows[{key,name,orderCount,salesValue}].

Run:  python reporting_tests.py --target staging [--from 2026-10-01 --to 2026-10-31]
"""
import argparse
import calendar
import csv
import io
import uuid
from datetime import date, datetime, timedelta, timezone

from sellora_qa import (COLOMBO, Api, parse_instant, Report, SetupError, audit_base, build_scenario, ci_get, colombo_today,
                        delivery_base, items_of, load_env, now_utc, poll, run_case, standard_args, wait_seconds)

STORY = "CSP-252"
CACHE_TTL = 60
FIELD_NAMES = {
    "sales": ("salesValue", "totalSales", "salesTotal", "sales"),
    "count": ("orderCount", "orders", "count"),
    "aov": ("averageOrderValue", "aov"),
    "collected": ("collected", "paymentsCollected", "collectedAmount"),
    "expected": ("expected", "paymentsExpected", "expectedAmount"),
    "outstanding": ("outstanding", "outstandingCredit"),
    "status": ("statusBreakdown", "orderStatusBreakdown", "byStatus"),
    "comparison": ("comparison", "compare", "previous"),
    "delta": ("deltas", "delta", "changes"),
    "key": ("key", "id", "groupId", "salesRepId", "agencyId", "territoryId", "productId"),
    "name": ("name", "label", "displayName"),
    "value": ("salesValue", "totalSales", "value", "sales", "total"),
}


TOTALS_KINDS = ("sales", "count", "aov", "collected", "expected", "outstanding")


def pick(obj, kind, default=None):
    """Field lookup that matches the Audit service contract: summary figures sit under "totals"
    (also inside "comparison"), and each delta is {absolute, percent}."""
    if kind in TOTALS_KINDS and isinstance(ci_get(obj, "totals"), dict):
        obj = ci_get(obj, "totals")
    for name in FIELD_NAMES[kind]:
        value = ci_get(obj, name)
        if value is not None:
            if isinstance(value, dict) and ci_get(value, "absolute") is not None and kind in TOTALS_KINDS:
                return ci_get(value, "absolute")
            return value
    return default


def money(value):
    return round(float(value or 0), 2)


class Reports:
    def __init__(self, s, base):
        self.s, self.api = s, Api(base, "reports")

    def summary(self, start, end, persona="admin", compare=None):
        query = f"from={start}&to={end}"
        if compare:
            query += f"&compareFrom={compare[0]}&compareTo={compare[1]}"
        return self.api.get(f"/api/reports/sales-summary?{query}", self.s.token(persona))

    def breakdown(self, group, start, end, persona="admin", sort=None):
        query = f"groupBy={group}&from={start}&to={end}" + (f"&sort={sort}" if sort else "")
        return self.api.get(f"/api/reports/sales-breakdown?{query}", self.s.token(persona))


def status_count(summary, status):
    breakdown = pick(summary, "status") or {}
    if isinstance(breakdown, dict):
        return ci_get(breakdown, status, 0)
    for row in breakdown:
        if ci_get(row, "status") == status:
            return ci_get(row, "count", 0)
    return 0


def deployed(reports, ctx, start, end):
    if "deployed" not in ctx:
        probe = reports.summary(start, end)
        ctx["deployed"] = probe.status not in (0, 404, 502)
        ctx["probe"] = probe
    if not ctx["deployed"]:
        raise SetupError(f"/api/reports not deployed ({ctx['probe'].status})")


# ---------------------------------------------------------------- the manual calculation from order records
def colombo_date(value):
    if not value:
        return None
    return parse_instant(value).astimezone(COLOMBO).date()


def load_orders(s, start, end):
    """Every order with its detail, for orders placed from 40 days before the range start."""
    if not s.order.configured:
        raise SetupError("ORDER_GW not set - needed for the manual calculation")
    floor = start - timedelta(days=40)
    orders, page = [], 1
    while True:
        response = s.order.get(f"/api/orders?page={page}&pageSize=100", s.token("admin"))
        rows = items_of(response.body)
        if response.status != 200 or not rows:
            break
        for row in rows:
            if colombo_date(ci_get(row, "orderDate")) and colombo_date(ci_get(row, "orderDate")) >= floor:
                detail = s.order.get(f"/api/orders/{ci_get(row, 'orderId')}", s.token("admin")).body
                orders.append(detail)
        if page * 100 >= ci_get(response.body, "totalCount", 0):
            break
        page += 1
    return orders


def calculate(orders, start, end, provinces=None):
    rows, sales, count, collected, statuses = [], 0.0, 0, 0.0, {}
    for order in orders:
        if provinces is not None and ci_get(order, "provinceId") not in provinces:
            continue
        confirmed_on = colombo_date(ci_get(order, "confirmedAt"))
        status = ci_get(order, "status")
        placed_on = colombo_date(ci_get(order, "orderDate"))
        if placed_on and start <= placed_on <= end:
            statuses[status] = statuses.get(status, 0) + 1
        counted = confirmed_on is not None and start <= confirmed_on <= end and status != "Cancelled"
        payment = ci_get(ci_get(order, "checkout") or {}, "payment") or {}
        paid = money(ci_get(payment, "amount")) if counted else 0.0
        if counted:
            sales += money(ci_get(order, "total"))
            count += 1
            collected += paid
        rows.append([ci_get(order, "orderReference"), status, ci_get(order, "fulfilmentType"), ci_get(order, "confirmedAt"),
                     confirmed_on, ci_get(order, "provinceId"), ci_get(order, "agencyId"), ci_get(order, "salesRepId"),
                     money(ci_get(order, "total")), paid, "yes" if counted else "no"])
    expected = round(sales, 2)
    return {"sales": round(sales, 2), "count": count, "aov": round(sales / count, 2) if count else 0.0,
            "expected": expected, "collected": round(collected, 2), "outstanding": round(expected - collected, 2),
            "statuses": statuses, "rows": rows}


def calc_csv(calc):
    buffer = io.StringIO()
    writer = csv.writer(buffer)
    writer.writerow(["orderReference", "status", "fulfilmentType", "confirmedAt", "confirmedOnColombo", "provinceId",
                     "agencyId", "salesRepId", "total", "paidAmount", "countedAsSale"])
    writer.writerows(calc["rows"])
    writer.writerow([])
    for key in ("sales", "count", "aov", "expected", "collected", "outstanding"):
        writer.writerow([key, calc[key]])
    return buffer.getvalue()


def compare_summary(r, label, summary, calc):
    pairs = [("total sales", money(pick(summary, "sales")), calc["sales"]),
             ("order count", pick(summary, "count"), calc["count"]),
             ("average order value", money(pick(summary, "aov")), calc["aov"]),
             ("payments expected", money(pick(summary, "expected")), calc["expected"]),
             ("payments collected", money(pick(summary, "collected")), calc["collected"]),
             ("outstanding", money(pick(summary, "outstanding")), calc["outstanding"])]
    for name, actual, expected in pairs:
        r.check(f"{label} {name} = {expected}", actual == expected, f"dashboard {actual}")
    r.check(f"{label} has an order status breakdown (FR-7.3)", pick(summary, "status") is not None)


# ---------------------------------------------------------------- cases
def q1_figures(s, r, ctx, rp):
    start, end = ctx["range"]
    deployed(rp, ctx, start, end)
    response = rp.summary(start, end)
    r.expect_status(f"Q1 sales-summary {start}..{end}", response, 200)
    orders = load_orders(s, start, end)
    ctx["orders"] = orders
    calc = calculate(orders, start, end)
    r.attach(f"calc/US-E7-2-Q1-{start}-{end}.csv", calc_csv(calc))
    r.note(f"manual calculation over {len(orders)} order records: {calc['count']} sales, {calc['sales']} LKR")
    compare_summary(r, "Q1", response.body, calc)
    daily = ci_get(response.body, "daily") or ci_get(response.body, "dailySeries") or []
    if daily:
        r.check("Q1 daily series sums to total sales", money(sum(money(pick(d, "value") or ci_get(d, "sales")) for d in daily))
                == calc["sales"], str(daily)[:200])
    for group in ("agency", "rep", "territory", "product"):
        rows = items_of(rp.breakdown(group, start, end).body)
        r.check(f"Q1 breakdown by {group} adds up to total sales", money(sum(money(pick(x, "value")) for x in rows)) == calc["sales"],
                f"sum {money(sum(money(pick(x, 'value')) for x in rows))}")

    if s.source != "kafka":
        r.manual("Q1 month-end case", ["Local only (python reporting_tests.py --target local): an order confirmed at "
                                       "23:30 Colombo on the last day (18:00 UTC) must count in that month, not the next"])
        return
    today = colombo_today()
    month_end = date(today.year, today.month, 1) - timedelta(days=1)
    month_start, next_start = month_end.replace(day=1), month_end + timedelta(days=1)
    before_month = rp.summary(month_start, month_end).body
    before_next = rp.summary(next_start, next_start + timedelta(days=2)).body
    late = datetime(month_end.year, month_end.month, month_end.day, 18, 0, tzinfo=timezone.utc)   # 23:30 Colombo, last day
    early = datetime(month_end.year, month_end.month, month_end.day, 19, 0, tzinfo=timezone.utc)  # 00:30 Colombo, day 1
    plan = [(s.ids["agencyId"], late, "old"), (str(uuid.uuid4()), late - timedelta(days=4), "old"),
            (str(uuid.uuid4()), late - timedelta(days=5), "old"), (s.ids["agencyId"], early, "new")]
    injected = {"old": 0.0, "new": 0.0}
    for index, (agency, when, bucket) in enumerate(plan):
        placed = None
        for event_type in ("OrderPlaced", "OrderConfirmed"):
            event = s.order_event(event_type, "ScheduledDelivery", 2 + index, "product1", confirmed_at=when, agency_id=agency)
            if event_type == "OrderPlaced":
                event["status"] = "PendingApproval"
                event.pop("confirmedAt", None)
                placed = event
            else:
                event.update({"orderId": placed["orderId"], "entityId": placed["orderId"],
                              "orderReference": placed["orderReference"]})
            s.publish_order_event(event)
        injected[bucket] += placed["total"]
    wait_seconds(CACHE_TTL + 10, "projection update + cache TTL")
    after_month = rp.summary(month_start, month_end).body
    after_next = rp.summary(next_start, next_start + timedelta(days=2)).body
    r.check(f"Q1 three agencies' orders incl. one at 23:30 Colombo on {month_end} (18:00 UTC) add {injected['old']} "
            f"to {month_end:%B}", round(money(pick(after_month, "sales")) - money(pick(before_month, "sales")), 2)
            == round(injected["old"], 2), f"{pick(before_month, 'sales')} -> {pick(after_month, 'sales')}")
    r.check(f"Q1 an order at 00:30 Colombo on {next_start} (19:00 UTC the day before) adds {injected['new']} to "
            f"{next_start:%B}, not {month_end:%B}", round(money(pick(after_next, "sales")) - money(pick(before_next, "sales")), 2)
            == round(injected["new"], 2), f"{pick(before_next, 'sales')} -> {pick(after_next, 'sales')} (a UTC date "
            "comparison puts it in the previous month)")


def q2_area_manager(s, r, ctx, rp):
    start, end = ctx["range"]
    deployed(rp, ctx, start, end)
    if not s.has("am"):
        raise SetupError("TOKEN_AREA_MANAGER_A not set")
    provinces = set(ci_get(s.scope("am"), "provinceIds") or [])
    calc = calculate(ctx.get("orders") or load_orders(s, start, end), start, end, provinces=provinces)
    r.attach(f"calc/US-E7-2-Q2-area-manager-{start}-{end}.csv", calc_csv(calc))
    response = rp.summary(start, end, persona="am")
    r.expect_status("Q2 Area Manager sales-summary", response, 200)
    compare_summary(r, "Q2 AM", response.body, calc)
    am_agencies = {str(row[6]) for row in calc["rows"]}
    for group in ("agency", "rep", "territory"):
        rows = items_of(rp.breakdown(group, start, end, persona="am").body)
        r.check(f"Q2 AM breakdown by {group} adds up to the AM's total", money(sum(money(pick(x, "value")) for x in rows))
                == calc["sales"])
        if group == "agency":
            foreign = [pick(x, "name") for x in rows if money(pick(x, "value")) > 0 and str(pick(x, "key")) not in am_agencies]
            r.check("Q2 no agency outside the AM's provinces has sales in the AM's view", not foreign, str(foreign))


def q3_comparison(s, r, ctx, rp):
    start, end = ctx["range"]
    deployed(rp, ctx, start, end)
    length = (end - start).days + 1
    prev_end = start - timedelta(days=1)
    prev_start = prev_end - timedelta(days=length - 1)
    combined = rp.summary(start, end, compare=(prev_start, prev_end)).body
    current, previous = rp.summary(start, end).body, rp.summary(prev_start, prev_end).body
    comparison = pick(combined, "comparison") or {}
    r.check("Q3 comparison values equal the previous period on its own",
            money(pick(comparison, "sales")) == money(pick(previous, "sales"))
            and pick(comparison, "count") == pick(previous, "count"), f"{comparison} vs {previous}"[:300])
    delta = pick(combined, "delta") or pick(comparison, "delta") or {}  # deltas sit inside comparison
    expected = round(money(pick(current, "sales")) - money(pick(previous, "sales")), 2)
    r.check(f"Q3 sales delta = current - previous = {expected}", money(pick(delta, "sales")) == expected, str(delta)[:200])


def q4_rep_breakdown(s, r, ctx, rp):
    start, end = ctx["range"]
    deployed(rp, ctx, start, end)
    rows = items_of(rp.breakdown("rep", start, end).body)
    zero_rep = s.env.get("ZERO_SALES_REP_ID")
    zeros = [x for x in rows if money(pick(x, "value")) == 0]
    if zero_rep:
        row = next((x for x in rows if str(pick(x, "key")) == zero_rep), None)
        r.check("Q4 the rep with no sales is listed with 0", row is not None and money(pick(row, "value")) == 0, str(row))
    elif zeros:
        r.check("Q4 at least one rep listed with 0 sales (dimension left-join)", True)
    else:
        r.blocked("Q4 rep with 0 sales listed (dimension left-join)",
                  f"every rep in the breakdown ({len(rows)}) has sales in the range - needs a second rep with no orders "
                  "(set ZERO_SALES_REP_ID)")
    for sort, kind in (("count", "count"), ("value", "value")):
        sorted_rows = items_of(rp.breakdown("rep", start, end, sort=sort).body)
        values = [money(pick(x, kind) if kind == "value" else ci_get(x, "orderCount", ci_get(x, "count"))) for x in sorted_rows]
        r.check(f"Q4 sort={sort} orders the rows", values == sorted(values, reverse=True) or values == sorted(values), str(values[:10]))


def q5_cancellation(s, r, ctx, rp):
    today = colombo_today()
    deployed(rp, ctx, today, today)
    before = rp.summary(today, today).body
    job = s.scheduled_job(quantity=1)
    if not job:
        raise SetupError("order did not confirm")
    order = job["_order"]
    wait_seconds(CACHE_TTL + 5, "cache TTL")
    confirmed = rp.summary(today, today).body
    total = money(ci_get(order, "total"))
    r.check(f"Q5 setup: confirmed order adds {total} to today's sales",
            round(money(pick(confirmed, "sales")) - money(pick(before, "sales")), 2) == total)
    response = s.cancel_order(order, reason="QA - reporting cancellation")
    r.check("Q5 setup: shop owner cancels inside the window", response.status in (200, 204), response.text[:200])
    wait_seconds(CACHE_TTL + 5, "cache TTL")
    after = rp.summary(today, today).body
    r.check("Q5 cancelled order leaves the sales total", money(pick(after, "sales")) == money(pick(before, "sales")),
            f"{pick(before, 'sales')} -> {pick(confirmed, 'sales')} -> {pick(after, 'sales')}")
    r.check("Q5 Cancelled count in the status breakdown rose by 1",
            status_count(after, "Cancelled") == status_count(confirmed, "Cancelled") + 1,
            f"{status_count(confirmed, 'Cancelled')} -> {status_count(after, 'Cancelled')}")


def q6_live_and_rebuild(s, r, ctx, rp):
    today = colombo_today()
    deployed(rp, ctx, today, today)
    before = money(pick(rp.summary(today, today).body, "sales"))
    job = s.scheduled_job(quantity=1)
    total = money(ci_get(job["_order"], "total")) if job else 0
    started = now_utc()
    seen = poll(lambda: round(money(pick(rp.summary(today, today).body, "sales")) - before, 2) == total or None,
                timeout=CACHE_TTL + 15, interval=5)
    elapsed = (now_utc() - started).total_seconds()
    r.check(f"Q6 new order appears within the cache TTL ({CACHE_TTL} s)", bool(seen), f"not seen after {elapsed:.0f} s")
    start, end = ctx["range"]
    snapshot = (rp.summary(start, end).text, rp.breakdown("agency", start, end).text)
    rebuild = rp.api.post("/api/reports/projections/rebuild", None, s.token("admin"), timeout=300)
    r.expect_status("Q6 rebuild projections as Company Admin", rebuild, 200, 202, 204)
    wait_seconds(CACHE_TTL + 5, "rebuild + cache TTL")
    after = (rp.summary(start, end).text, rp.breakdown("agency", start, end).text)
    r.check("Q6 summary identical after replay", after[0] == snapshot[0], f"{snapshot[0][:150]} / {after[0][:150]}")
    r.check("Q6 agency breakdown identical after replay", after[1] == snapshot[1])
    if s.has("am"):
        r.expect_status("Q6 Area Manager cannot rebuild", rp.api.post("/api/reports/projections/rebuild", None, s.token("am")), 403)


def q7_access(s, r, ctx, rp):
    start, end = ctx["range"]
    deployed(rp, ctx, start, end)
    for persona in ("op", "rep", "shop"):
        if s.has(persona):
            r.expect_status(f"Q7 {persona} sales-summary", rp.summary(start, end, persona=persona), 403)
            r.expect_status(f"Q7 {persona} sales-breakdown", rp.breakdown("rep", start, end, persona=persona), 403)
    future = colombo_today() + timedelta(days=400)
    empty = rp.summary(future, future + timedelta(days=7))
    r.check("Q7 empty range -> 200 with zero sales", empty.status == 200 and money(pick(empty.body, "sales")) == 0, empty.text[:200])


def main():
    parser = standard_args(argparse.ArgumentParser(description=__doc__,
                                                   formatter_class=argparse.RawDescriptionHelpFormatter))
    today = colombo_today()
    parser.add_argument("--from", dest="start", default=str(today.replace(day=1)))
    parser.add_argument("--to", dest="end", default=str(today.replace(day=calendar.monthrange(today.year, today.month)[1])))
    args = parser.parse_args()
    env = load_env(args.env)
    report = Report("reporting")
    base = audit_base(env, args.target)
    print(f"Target: {args.target} ({base})")
    from sellora_qa import require_api
    require_api(args, report, base, "AUDIT_GW")
    s = build_scenario(args, env, report, delivery_base(env, args.target))
    from sellora_qa import audit_preflight
    audit_preflight(s, report, base)
    rp = Reports(s, base)
    ctx = {"range": (date.fromisoformat(args.start), date.fromisoformat(args.end))}

    report.story(STORY, "US-E7-2 Reporting dashboards")
    run_case(report, "US-E7-2-Q1", "Figures match a manual calculation", q1_figures, s, report, ctx, rp)
    run_case(report, "US-E7-2-Q2", "Area Manager scope", q2_area_manager, s, report, ctx, rp)
    run_case(report, "US-E7-2-Q3", "Comparison period", q3_comparison, s, report, ctx, rp)
    run_case(report, "US-E7-2-Q4", "Rep breakdown", q4_rep_breakdown, s, report, ctx, rp)
    run_case(report, "US-E7-2-Q5", "Cancellation", q5_cancellation, s, report, ctx, rp)
    run_case(report, "US-E7-2-Q6", "Live and replay", q6_live_and_rebuild, s, report, ctx, rp)
    run_case(report, "US-E7-2-Q7", "Access", q7_access, s, report, ctx, rp)
    report.finish(args.evidence, args.target, base)


if __name__ == "__main__":
    main()