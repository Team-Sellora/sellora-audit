# Sellora Audit: Selenium UI Testing Report

End-to-end Selenium UI tests for the Audit log, Reports and Exports pages of the Sellora web app, run locally and in GitHub Actions.

[![Selenium UI tests (Audit)](https://github.com/Team-Sellora/sellora-audit/actions/workflows/selenium.yml/badge.svg?branch=test/selenium-audit)](https://github.com/Team-Sellora/sellora-audit/actions/workflows/selenium.yml?query=branch%3Atest%2Fselenium-audit)
![Tests](https://img.shields.io/badge/tests-11%20passed-brightgreen)
![Known defects](https://img.shields.io/badge/known%20defects-1-orange)
![Python](https://img.shields.io/badge/Python-3.12-3776AB)
![Selenium](https://img.shields.io/badge/Selenium-4-43B02A)
![pytest](https://img.shields.io/badge/pytest-0A9EDC)
![Page Object Model](https://img.shields.io/badge/design-Page%20Object%20Model-6f42c1)

## At a glance

| Item | Value |
|------|-------|
| Test cases | 12 (TC-AUD-01 to TC-AUD-12) |
| Result | 11 passed, 1 expected failure (TC-AUD-05, `xfail` for DEF-01) |
| Local runs | First run 2026-10-21: TC-AUD-05 failed and led to DEF-01. Latest run 2026-10-22: 11 passed, 0 failed, 1 xfail |
| Pipeline result | Run [#39625203542](https://github.com/Team-Sellora/sellora-audit/actions/runs/39625203542) on commit `48dfd8b`: *success*, 11 passed, 0 failed, 1 xfail |
| Total run time | Latest local run 191.0 s (3 min 11 s). Pipeline run 112.9 s (1 min 53 s) |
| Product defects found | 1 ([DEF-01](#6-defect-found-def-01), Medium) |
| Test suite issues found and fixed | 0. No test suite issues were found or fixed in this suite |

## Contents

1. [Overview and scope](#1-overview-and-scope)
2. [Tech stack and design](#2-tech-stack-and-design)
3. [Project structure](#3-project-structure)
4. [Test cases](#4-test-cases)
5. [Results](#5-results)
6. [Defect found: DEF-01](#6-defect-found-def-01)
7. [Design lessons applied from the Delivery suite](#7-design-lessons-applied-from-the-delivery-suite)
8. [CI pipeline](#8-ci-pipeline)
9. [How to run locally](#9-how-to-run-locally)
10. [Evidence](#10-evidence)
11. [Limitations and next steps](#11-limitations-and-next-steps)
12. [Service documentation](#service-documentation)

## 1. Overview and scope

**What is tested.** Three pages of the Sellora Management Console that are backed by this Audit service (Epic E7), on the shared test site `http://test.selloralk.tech`:

| Page | Story | What the tests check |
|------|-------|----------------------|
| Audit log | US-E7-1 | Page heading and immutability notice, the Audit Entries table, filters and Reset, invalid input, the entry details drawer, cryptographic chain verification |
| Reports (Reporting dashboard) | US-E7-2 | Date presets, From/To dates, Compare previous period, Sales Breakdown tabs |
| Exports (My exports panel) | US-E7-3 | An Export CSV request shows up in My exports with a valid status |

Login through the identity server is also covered, because every test starts from it.

**Why UI tests.** The backend already has unit and API tests (`tests/Sellora.AuditService.Tests`). These Selenium tests add the user's view: they check that the web app calls the service correctly, shows the data, and reports errors in a way a user can understand. DEF-01 is an example of a problem that only shows up at this level: the API contract is right, but the user gets wrong results without any message.

**Scope.** The tests are read-only. The only write is one harmless CSV export that TC-AUD-12 queues. Nothing is rebuilt, changed or deleted. The tests run as a CompanyAdmin test account, so every page and the Verify integrity panel are visible.

## 2. Tech stack and design

| Item | Use |
|------|-----|
| Python 3.12+ | Language (3.12 in the pipeline) |
| Selenium 4 | Browser automation. Selenium Manager downloads the matching ChromeDriver |
| pytest 8 | Test runner, fixtures and markers (`smoke`, `audit`, `reports`, `exports`, `negative`) |
| pytest-html | Self-contained HTML report with a screenshot for every test |
| Google Chrome | Headless by default (`--headless=new`, 1920x1080) |

**Design choices**

- **Page Object Model.** Locators and page actions live in `pages/`. Tests only call page methods and assert. Locators prefer `data-testid`, `aria-label` and visible label text over CSS layout.
- **Explicit waits only.** Every wait is a `WebDriverWait` on a condition (table loaded, drawer ready, dates changed, export listed). There are no fixed sleeps.
- **A screenshot for every test.** A hook in `conftest.py` saves `reports/screenshots/<test>-<outcome>.png` after each test (or after a failed setup) and adds it to the HTML report.

```mermaid
flowchart LR
    A[GitHub Actions<br/>or local PowerShell] --> B[pytest]
    B --> C[Page objects<br/>pages/]
    C --> D[Headless Chrome<br/>Selenium 4]
    D --> E[Test site<br/>test.selloralk.tech]
    B --> F[reports/report.html<br/>reports/junit.xml]
    D --> G[reports/screenshots/]
```

## 3. Project structure

All suite files live in [`selenium-tests/`](selenium-tests). The pipeline is defined in [`.github/workflows/selenium.yml`](.github/workflows/selenium.yml).

<details>
<summary>Show the project tree</summary>

```text
selenium-tests/
├── conftest.py              # Chrome driver fixture, base_url fixture, screenshot hook
├── pytest.ini               # test path, HTML report options, markers
├── requirements.txt         # selenium, pytest, pytest-html
├── .gitignore               # ignores reports/, caches and .venv/
├── test-cases.md            # test case table with steps, expected and actual results
├── defects.md               # defects found by the suite (DEF-01)
├── README.md                # the suite's copy of this report
├── docs/
│   └── images/              # evidence screenshots used in this report
├── pages/
│   ├── __init__.py
│   ├── base_page.py         # shared waits, stale-safe reads, sidebar navigation, error banner checks
│   ├── login_page.py        # identity server login with LOGIN_TIMEOUT, consent page and one retry
│   ├── audit_log_page.py    # Audit log: filters, table, entry drawer, Verify integrity
│   ├── reports_page.py      # Reporting dashboard: presets, dates, compare toggle, breakdown tabs, export
│   └── my_exports_panel.py  # My exports panel: open, load, read export rows
├── tests/
│   ├── test_login.py        # TC-AUD-01
│   ├── test_audit_log.py    # TC-AUD-02 to TC-AUD-07
│   ├── test_reports.py      # TC-AUD-08 to TC-AUD-11
│   └── test_exports.py      # TC-AUD-12
└── reports/                 # created by each run, not committed: report.html, junit.xml, screenshots/, downloads/
```

</details>

## 4. Test cases

Full steps and expected results are in [selenium-tests/test-cases.md](selenium-tests/test-cases.md). Durations are from the JUnit reports of the latest local run and of the pipeline run.

| ID | Title | Area | Type | Result | Local (s) | Pipeline (s) |
|----|-------|------|------|--------|----------:|-------------:|
| TC-AUD-01 | Login succeeds | Login | Functional | ✅ Passed | 12.9 | 5.4 |
| TC-AUD-02 | Audit log page opens | Audit log | Navigation | ✅ Passed | 20.4 | 29.2 |
| TC-AUD-03 | Audit Entries table structure | Audit log | Functional | ✅ Passed | 15.1 | 7.5 |
| TC-AUD-04 | Event Type filter and Reset | Audit log | Functional | ✅ Passed | 16.3 | 7.9 |
| TC-AUD-05 | Non-UUID Aggregate ID | Audit log | Negative | ⚠️ XFail (DEF-01) | 15.1 | 7.0 |
| TC-AUD-06 | Audit entry details drawer | Audit log | Functional | ✅ Passed | 16.2 | 8.1 |
| TC-AUD-07 | Verify chain integrity | Audit log | Functional | ✅ Passed | 15.6 | 8.1 |
| TC-AUD-08 | Reports page opens | Reports | Navigation | ✅ Passed | 13.7 | 7.6 |
| TC-AUD-09 | Date presets | Reports | Functional | ✅ Passed | 13.5 | 6.6 |
| TC-AUD-10 | Compare previous period | Reports | Functional | ✅ Passed | 13.5 | 6.2 |
| TC-AUD-11 | Sales Breakdown tabs | Reports | Functional | ✅ Passed | 16.6 | 7.7 |
| TC-AUD-12 | Export CSV to My exports | Exports | Export | ✅ Passed | 19.4 | 10.4 |

Each test logs in with a fresh browser, so every duration includes the login.

## 5. Results

> [!WARNING]
> **DEF-01 (product defect, Medium): a non-UUID Aggregate ID filter is silently ignored.**
> On the Audit log page, entering `not-a-valid-uuid` in Aggregate ID shows all 116 entries and no error message. The API spec expects `400 Bad Request`. TC-AUD-05 tracks it as `xfail(strict=True)`. Details in [section 6](#6-defect-found-def-01).

| Run | Where | Date | Passed | Failed | XFail | Total time |
|-----|-------|------|-------:|-------:|------:|-----------:|
| Latest local run | Windows 11, Python 3.14, Chrome | 2026-10-22 | 11 | 0 | 1 | 191.0 s (3 min 11 s) |
| Pipeline run [#39625203542](https://github.com/Team-Sellora/sellora-audit/actions/runs/39625203542) | GitHub Actions `ubuntu-latest`, Python 3.12, headless Chrome | 2026-10-21 | 11 | 0 | 1 | 112.9 s (1 min 53 s) |

- **Pass rate:** 11 of 12 tests passed (91.7 %). The 12th test, TC-AUD-05, failed as expected because of a known product defect. There were no unexpected failures.
- **Pipeline:** the run on commit `48dfd8b` finished with conclusion *success*. Every step passed, including *Run Selenium tests* and *Upload report*.
- **JUnit:** JUnit XML has no separate xfail count, so `junit.xml` lists TC-AUD-05 as `skipped` with type `pytest.xfail`.

**How the xfail came about**

1. In the first local run (2026-10-21), TC-AUD-05 **failed**. Its screenshot shows `not-a-valid-uuid` in Aggregate ID, the "Timeline mode active" notice, and the table still listing all 116 entries.
2. The failure was checked against the API spec and the backend tests. The test was correct and the product was wrong, so this is a real product defect (DEF-01).
3. The test was **kept unchanged** rather than weakened to match the wrong behaviour. Its assertions still describe the correct behaviour, and it is marked as a known defect:

   ```python
   @pytest.mark.xfail(strict=True, reason="Known defect: non-UUID Aggregate ID is ignored and all entries are shown (see defects.md)")
   ```

4. Because of `strict=True`, the test cannot silently pass. When DEF-01 is fixed, the test passes, pytest reports it as **XPASS(strict)** and the run fails. That is the signal to remove the marker, so the test goes back to being a normal regression test.

## 6. Defect found: DEF-01

**Non-UUID Aggregate ID filter is silently ignored.** This is a product defect: a bug in the web app, not in the tests. It is also recorded in [selenium-tests/defects.md](selenium-tests/defects.md).

| Field | Value |
|-------|-------|
| ID | DEF-01 |
| Type | Product defect (web app) |
| Found by | TC-AUD-05 (`test_invalid_aggregate_id_is_handled`) |
| Page | Audit log, filter form |
| Environment | `http://test.selloralk.tech`, CompanyAdmin test account, Chrome |
| Severity | **Medium.** The user gets wrong search results with no warning, which can mislead an audit search. No data is lost or exposed, and entering a valid UUID works. |
| Status | Open. Tracked in the suite as `xfail(strict=True)` |

**Steps to reproduce**

1. Log in and open **Audit log** from the sidebar.
2. Type `not-a-valid-uuid` in **Aggregate ID**.
3. Click **Apply filters**.

**Expected result.** A malformed Aggregate ID is rejected with `400 Bad Request`, and the page shows its error banner ("Unable to load audit entries"). The API spec [docs/US-E7-1.md](docs/US-E7-1.md) says that for `GET /api/audit` "a malformed value → 400". The backend test `ApiSecurityTests.A_malformed_filter_is_a_bad_request` expects `400 Bad Request` for `aggregateId=not-a-guid`.

**Actual result.** No error banner or message is shown. "Timeline mode active" appears, but the filter is silently dropped and the table lists all entries (116 total, the same as with no filter). The user is not told that the filter was ignored.

**Evidence**

![DEF-01: the table still shows all 116 entries after an invalid Aggregate ID](selenium-tests/docs/images/def-01-invalid-aggregate-id.png)

*TC-AUD-05, first local run: `not-a-valid-uuid` is in Aggregate ID and "Timeline mode active" is shown, but Audit Entries still shows 116 total.*

**Why the test is marked `xfail(strict=True)`.** The test still asserts the correct behaviour, so the suite stays green while the defect is open, and the defect stays visible in every report. When the bug is fixed, the test passes, pytest reports **XPASS(strict)** and the run fails. That forces the marker to be removed, and TC-AUD-05 becomes a normal regression test again.

**Notes.** The web form sends the value without checking it, and its placeholder says "UUID or reference", while the API only accepts a GUID for `aggregateId`.

**Suggested fix**

- Validate Aggregate ID in the web filter form and show an inline message when it is not a UUID, or
- make sure the 400 from the backend reaches the page and is shown in the error banner instead of the filter being dropped.

When the fix is deployed, remove the `xfail` marker from TC-AUD-05.

## 7. Design lessons applied from the Delivery suite

These lessons from the earlier Delivery suite were built in from the first commit. They are design choices, not fixes: no test suite issues were found or fixed in this suite.

| Lesson | How it is applied |
|--------|-------------------|
| **Navigation by the sidebar** | The web app keeps the login token in memory only, so a full page load from a typed URL would lose it. After login, every page is opened by clicking its sidebar link. |
| **Robust login** | Each login step waits up to `LOGIN_TIMEOUT` seconds (default 60). After submit, the login accepts either the app shell (sidebar) or a consent page, and the whole login is retried once if a step times out. |
| **Stale-element-safe reads** | `BasePage.read()` retries a read when React re-renders the element, and the waits ignore `StaleElementReferenceException`. |

## 8. CI pipeline

Workflow: [`.github/workflows/selenium.yml`](.github/workflows/selenium.yml), name **Selenium UI tests (Audit)**.

| Item | Value |
|------|-------|
| Triggers | `push` to `test/selenium-audit`, and manual run (`workflow_dispatch`) only |
| Runner | `ubuntu-latest`, timeout 25 minutes |
| Working directory | `selenium-tests` |
| Target | `BASE_URL=http://test.selloralk.tech` |
| Secrets | `SELLORA_TEST_USER`, `SELLORA_TEST_PASSWORD` (mapped to `TEST_USER` and `TEST_PASSWORD`) |
| Artifact | `selenium-audit-report` (the whole `selenium-tests/reports/` folder) |

**Steps**

1. `actions/checkout@v4`
2. `actions/setup-python@v5` with Python 3.12
3. Install dependencies: `pip install -r requirements.txt`
4. Run Selenium tests: `pytest --html=reports/report.html --self-contained-html --junitxml=reports/junit.xml`
5. Upload report: `actions/upload-artifact@v4`. This runs with `if: always()`, so the report is uploaded even when tests fail.

The runner already has Google Chrome, and Selenium Manager gets the matching driver, so no driver setup step is needed.

**Secrets.** Add `SELLORA_TEST_USER` and `SELLORA_TEST_PASSWORD` under *Settings → Secrets and variables → Actions* as repository secrets. They must be the login of a CompanyAdmin test account on the test site.

**Download the report**

- In the browser: *Actions → Selenium UI tests (Audit) → the run → Artifacts → selenium-audit-report*. Unzip it and open `report.html`.
- With the GitHub CLI:

  ```powershell
  gh run download <run-id> --repo Team-Sellora/sellora-audit --name selenium-audit-report --dir selenium-audit-report
  ```

The artifact holds `report.html` (with screenshots embedded), `junit.xml` and `screenshots/`.

## 9. How to run locally

Needs Google Chrome and Python 3.12 or later. Commands are for Windows PowerShell, run from the repository root.

**Environment variables**

| Name | Default | Purpose |
|------|---------|---------|
| `BASE_URL` | `http://test.selloralk.tech` | Site under test |
| `TEST_USER` | (required) | Login of a CompanyAdmin test account |
| `TEST_PASSWORD` | (required) | Password of that account |
| `HEADLESS` | `1` | `0` opens a visible browser window |
| `LOGIN_TIMEOUT` | `60` | Seconds to wait for each login step |

<details>
<summary>Show the PowerShell commands</summary>

**Set up**

```powershell
cd selenium-tests
py -3.12 -m venv .venv
.\.venv\Scripts\python -m pip install -r requirements.txt
```

**Set the login**

```powershell
$env:TEST_USER = "<test account login>"
$env:TEST_PASSWORD = Read-Host "Password"
```

**Headless run (default)**

```powershell
.\.venv\Scripts\python -m pytest --junitxml=reports/junit.xml
```

**Headed run (watch the browser)**

```powershell
$env:HEADLESS = "0"
.\.venv\Scripts\python -m pytest --junitxml=reports/junit.xml
Remove-Item Env:HEADLESS
```

**Run part of the suite**

```powershell
.\.venv\Scripts\python -m pytest -m audit
.\.venv\Scripts\python -m pytest tests/test_reports.py
```

</details>

**Output**

| Path | Content |
|------|---------|
| `reports/report.html` | Self-contained HTML report with a screenshot under each test |
| `reports/junit.xml` | JUnit XML (only when `--junitxml` is given) |
| `reports/screenshots/` | One PNG per test, named `<test>-<outcome>.png` |
| `reports/downloads/` | Files downloaded by the browser during the run |

## 10. Evidence

Screenshots were taken by the suite itself on the test site. Apart from DEF-01, all of them are from the latest local run.

**TC-AUD-02: Audit log page**

![Audit log page](selenium-tests/docs/images/audit-log-page.png)

*The Audit log page opened from the sidebar, with the immutability description, Cryptographic Chain Verification, the filter form and the Audit Entries table (116 total).*

**TC-AUD-07: Verify integrity**

![Verify integrity result](selenium-tests/docs/images/verify-integrity.png)

*After Verify integrity, "Chain integrity verified" shows that all 116 sequential entries have valid hash continuity.*

**TC-AUD-11: Reporting dashboard**

![Reports dashboard](selenium-tests/docs/images/reports-dashboard.png)

*The Reporting dashboard with the Last 90 days preset, the summary cards, Daily Sales Trend and Orders by Status charts, and the Sales Breakdown tabs below.*

**TC-AUD-12: My exports**

![My exports with the new CSV export](selenium-tests/docs/images/my-exports-csv.png)

*After Export CSV, a new "Sales breakdown" CSV export is listed in My exports with status Ready.*

**TC-AUD-05: DEF-01**

![DEF-01 defect](selenium-tests/docs/images/def-01-invalid-aggregate-id.png)

*With `not-a-valid-uuid` in Aggregate ID, the table still shows all 116 entries and no message.*

The full HTML report and JUnit XML for each run are in `selenium-tests/reports/` locally and in the `selenium-audit-report` artifact of the pipeline run.

## 11. Limitations and next steps

**Limitations**

- The suite depends on the shared test site and its data. Some checks adapt to the data: TC-AUD-06 is skipped if there are no audit entries, TC-AUD-07 if the account cannot verify, and TC-AUD-11 if there are no confirmed sales in the last 90 days. Downtime or data changes on the test site can affect results.
- All tests run as one CompanyAdmin account, so role-based behaviour is not covered yet.
- Only Google Chrome is tested.
- TC-AUD-12 adds one export to the account's My exports list on each run.

**Next steps**

1. Fix DEF-01 and remove the `xfail` marker from TC-AUD-05.
2. Add role-based tests: an Area Manager sees only entries in their scope, and a Sales Rep cannot open the Audit log.
3. Run the suite on more browsers (Firefox, Edge), for example as a matrix in the pipeline.

---

## Service documentation

# Sellora Audit Service

Epic E7: every business event from the five Sellora topics is stored in an
append-only, hash-chained audit log per company, and sales dashboards are
built from that log.

| Story | What it delivers | Notes |
|---|---|---|
| US-E7-1 (CSP-250) | Immutable audit log: search, entry detail, chain verification | [docs/US-E7-1.md](docs/US-E7-1.md) |
| US-E7-2 (CSP-252) | Reporting projections and sales dashboards (summary, breakdown, rebuild) | [docs/US-E7-2.md](docs/US-E7-2.md) |
| US-E7-3 (CSP-253) | Asynchronous CSV and PDF exports of the reports and audit extracts | [docs/US-E7-3.md](docs/US-E7-3.md) |

Same layout as sellora-order: `Domain` → `Application` → `Infrastructure` → `Api`, plus `tests`. .NET 8.

## Run it locally

Prerequisites: .NET 8 SDK, Docker, and Kafka from `sellora-infra`'s compose file on `localhost:9092`.

```bash
# 1. Database with the real roles (audit_owner, audit_app) on port 5438.
cp .env.example .env            # local-only passwords
docker compose up -d audit-db

# 2. Migrations, as audit_owner (the app never migrates itself).
#    ADMIN_CONNECTION re-applies the grants afterwards; it needs psql on the PATH.
ADMIN_CONNECTION="postgresql://postgres:postgres_dev_pw@localhost:5438/audit_db" \
AUDIT_OWNER_PASSWORD=audit_owner_dev_pw AUDIT_APP_PASSWORD=audit_app_dev_pw \
  ./scripts/run-migrations.sh

# 3. Runtime connection string, as audit_app.
dotnet user-secrets --project src/Sellora.AuditService.Api set "ConnectionStrings:Default" \
  "Host=localhost;Port=5438;Database=audit_db;Username=audit_app;Password=audit_app_dev_pw"

# 4. Run (http://localhost:5208, Swagger at /swagger in Development).
dotnet run --project src/Sellora.AuditService.Api
```

With Docker: `docker build -t sellora-audit .` then `docker run -p 8080:8080 -e ConnectionStrings__Default=... sellora-audit`.
The container listens on 8080; `GET /health` returns `200 {"status":"Healthy"}` and does not depend on the database or Kafka.

## API

| Route | Roles |
|---|---|
| `GET /api/audit?orderReference&aggregateType&aggregateId&actorUserId&eventType&from&to&page&pageSize` | CompanyAdmin, AreaManager |
| `GET /api/audit/{auditEntryId}` | CompanyAdmin, AreaManager |
| `GET /api/audit/verify` | CompanyAdmin |
| `GET /api/reports/sales-summary?from&to[&compareFrom&compareTo]` | CompanyAdmin, AreaManager |
| `GET /api/reports/sales-breakdown?groupBy&from&to[&sort&direction]` | CompanyAdmin, AreaManager |
| `POST /api/reports/projections/rebuild` | CompanyAdmin |
| `POST /api/reports/export` | CompanyAdmin, AreaManager (`audit-extract`: CompanyAdmin) |
| `GET /api/reports/export` | CompanyAdmin, AreaManager (own exports) |
| `GET /api/reports/export/{exportId}` | CompanyAdmin, AreaManager (own exports) |
| `GET /api/reports/export/{exportId}/file` | CompanyAdmin, AreaManager (own exports) |

The company always comes from the token. Area Managers see only their provinces (from Organization). Examples: `src/Sellora.AuditService.Api/Sellora.AuditService.Api.http`.

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings__Default` | `audit_app` on `localhost:5438`, no password | Runtime role: SELECT and INSERT on `audit.audit_entry`; full DML on the `reporting` schema |
| `ConnectionStrings__Owner` | — | Migrations only (`scripts/run-migrations.sh`, `dotnet ef`) |
| `Kafka__BootstrapServers` | `localhost:9092` | |
| `Kafka__SaslUsername` / `Kafka__SaslPassword` | empty | Confluent Cloud API key and secret; SASL_SSL/PLAIN is used when a username is set |
| `Kafka__Topics` | the five `sellora.*.v1` topics | Source topics |
| `Kafka__ConsumerGroupId` | `sellora.audit.v1` | |
| `Kafka__DeadLetterTopic` | `sellora.audit.dead-letter.v1` | [contract](docs/contracts/audit-dead-letter.v1.md) |
| `Kafka__AutoOffsetReset` | `Earliest` | First deploy back-fills what the topics still retain |
| `Kafka__RetryDelaySeconds` | `5` | Wait before replaying after a failure |
| `Dependencies__Organization__BaseUrl` | `http://localhost:8081` | Area Manager provinces (`GET /api/me/scope`) and territory names (`GET /api/hierarchy`) |
| `CallerScope__CacheSeconds` | `60` | Cache for an Area Manager's provinces |
| `Reporting__CacheSeconds` | `60` | In-process cache for report responses (no Redis); `0` turns it off |
| `Exports__PollSeconds` | `5` | How often the export worker looks for queued jobs |
| `Exports__ExpiryHours` | `24` | How long a finished export can be downloaded |
| `Exports__CleanupMinutes` | `60` | How often expired export files are removed |
| `Cors__AllowedOrigins` | `http://localhost:5173` | |
| `Jwt__*` | WSO2 values, as in sellora-order | Raw claim names (`roles`, `companyId`, `sub`) |

## Tests

```bash
dotnet test                                                   # everything; Docker must be running
dotnet test --filter "FullyQualifiedName!~Integration&FullyQualifiedName!~AuditApiTests"   # no Docker needed
```

- `Unit`: envelope normalisation against real producer fixtures (`tests/.../Fixtures`), hash chain with fixed vectors, consumer settings and mapping, role claims, report maths, the Colombo sale date, the projection rules, and export validation, status rules, CSV and PDF writers.
- `Integration`: Testcontainers PostgreSQL 16 with the real roles script and migrations (the runtime role cannot UPDATE/DELETE/TRUNCATE, tamper detection, idempotent and concurrent appends, scoping, reporting figures, projections in the append transaction and rebuild) and Testcontainers Kafka (all five topics, dead-letter, replay after a failure without duplicates, a missing topic).
- `Api`: the real host's routes are read only (the only POSTs are the rebuild and the export submission); 401, 403, 400, 404, 405, 409, 410 and 503 behaviour; Area Manager scoping against PostgreSQL, for the audit, report and export routes.

## Load test data (US-E7-1-Q7)

```bash
psql "host=localhost port=5438 dbname=audit_db user=audit_owner" \
  -v company_id=<company uuid> -v entries=100000 -v orders=20000 -v days=90 \
  -f scripts/seed-audit-entries.sql
```

Continues the company's chain with valid hashes, so verify still passes. Test databases only.
Measured locally (PostgreSQL 16 in Docker, 100,000 entries): seeding 10.7 s, verify 3.1 s, order-reference search 0.3 ms and a 7-day page 0.15 ms at the database.
