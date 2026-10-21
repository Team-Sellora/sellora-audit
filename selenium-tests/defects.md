# Known defects

## DEF-01: Non-UUID Aggregate ID filter is silently ignored

Found by: TC-AUD-05 (`test_invalid_aggregate_id_is_handled`)
Environment: http://test.selloralk.tech, CompanyAdmin

Steps:
1. Log in and open "Audit log" from the sidebar.
2. Type `not-a-valid-uuid` in Aggregate ID.
3. Click "Apply filters".

Expected: no matching entries, or a friendly message that the Aggregate ID is not valid. The API spec (docs/US-E7-1.md) says a malformed filter value returns 400, and `ApiSecurityTests.A_malformed_filter_is_a_bad_request` expects 400 for `aggregateId=not-a-guid`.

Actual: no error banner or message. "Timeline mode active" is shown, but the table lists all entries (116 total, the same as with no filter). The user is told nothing about the ignored filter.

Notes: the web form sends the value without checking it, and its placeholder says "UUID or reference", but the API only accepts a GUID for `aggregateId`.
Screenshot: `reports/screenshots/test_invalid_aggregate_id_is_handled-failed.png` (first run, before xfail).
