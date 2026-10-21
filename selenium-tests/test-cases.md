# Audit UI test cases (Selenium)

Target: `BASE_URL` (default `http://test.selloralk.tech`). Every test logs in first and opens pages from the sidebar.

| ID | Title | Steps | Expected result | Actual | Status |
|----|-------|-------|-----------------|--------|--------|
| TC-AUD-01 | Login succeeds | 1. Open BASE_URL. 2. Click "Sign in" if shown. 3. Enter TEST_USER and TEST_PASSWORD and submit. 4. Accept consent if asked. | App sidebar is shown with "Audit log" and "Reports". | As expected. | Pass |
| TC-AUD-02 | Audit log page opens | 1. Log in. 2. Click "Audit log" in the sidebar. | Heading "Audit log" and the immutability notice ("immutable and append-only") are shown. | As expected. | Pass |
| TC-AUD-03 | Audit Entries table structure | 1. Open the Audit log page. 2. Wait for the table to load. | "Audit Entries" table with columns POS, EVENT TYPE, AGGREGATE, ORDER REF, ACTOR, RECORDED TIME, CORRELATION, ACTION; rows or the empty text; no error banner. | As expected. | Pass |
| TC-AUD-04 | Event Type filter and Reset | 1. Type "order.placed" in Event Type. 2. Click "Apply filters". 3. Fill all seven filter fields. 4. Click "Reset". | Filter is applied (shown in the URL) and the table loads without an error banner; after Reset every filter field is empty. | As expected. | Pass |
| TC-AUD-05 | Non-UUID Aggregate ID (negative) | 1. Type "not-a-valid-uuid" in Aggregate ID. 2. Click "Apply filters". | No results, or the friendly "Unable to load audit entries" message; never a raw error page or stack trace. | No error banner or message; "Timeline mode active" shown but the table lists all entries (116 total, same as no filter). See defects.md DEF-01. | Fail (known defect) |
| TC-AUD-06 | Audit entry details drawer | 1. Click "Inspect" on the first entry. 2. Close the drawer. | Drawer opens with title "Entry #..." and its details, then closes. Skipped if there are no entries. | As expected. | Pass |
| TC-AUD-07 | Verify chain integrity | 1. In "Cryptographic Chain Verification", click "Verify integrity". | "Chain integrity verified", or "compromised" with a reason; no error banner. Skipped if the role cannot verify. | As expected. | Pass |
| TC-AUD-08 | Reports page opens | 1. Log in. 2. Click "Reports" in the sidebar. | Heading "Reporting dashboard"; presets This month, Last month, Last 90 days; From date and To date; Summary/Breakdown select; Export CSV and Export PDF buttons. | As expected. | Pass |
| TC-AUD-09 | Date presets | 1. Click "Last month". 2. Click "Last 90 days". | Dates change each time (Last month starts on day 1; Last 90 days spans about 90 days); summary or empty state shown; no error banner. | As expected. | Pass |
| TC-AUD-10 | Compare previous period | 1. Switch "Compare previous period" on. 2. Switch it off. | Comparison dates appear when on and hide when off; no error banner. | As expected. | Pass |
| TC-AUD-11 | Sales Breakdown tabs | 1. Click "Last 90 days". 2. Click Rep, Territory, Agency, Product. | Each tab is selected and shows table rows or "No confirmed sales for this breakdown"; no error banner. Skipped if the period has no sales. | As expected. | Pass |
| TC-AUD-12 | Export CSV to My exports | 1. Open "My exports" (Show). 2. Click "Export CSV" (mode Breakdown). | A new "Sales breakdown" CSV export is listed with status Queued, Running, Ready or Failed within 60 seconds. | As expected. | Pass |
