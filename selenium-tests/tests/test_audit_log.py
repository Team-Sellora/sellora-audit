from urllib.parse import unquote

import pytest

from pages import audit_log_page as al
from pages.audit_log_page import AuditLogPage
from pages.login_page import LoginPage

EXPECTED_COLUMNS = [
    "POS",
    "EVENT TYPE",
    "AGGREGATE",
    "ORDER REF",
    "ACTOR",
    "RECORDED TIME",
    "CORRELATION",
    "ACTION",
]
TEXT_FILTERS = {
    "Order Reference": "ORD-1001",
    "Event Type": "order.placed",
    "Aggregate Type": "Order",
    "Aggregate ID": "00000000-0000-0000-0000-000000000000",
    "Actor User ID": "00000000-0000-0000-0000-000000000000",
}
RAW_ERROR_MARKERS = ("Exception", "Traceback", "stack trace", "at System.")


@pytest.fixture
def audit_page(driver, base_url):
    LoginPage(driver, base_url).login()
    return AuditLogPage(driver).open()


@pytest.mark.audit
def test_audit_page_opens_with_heading_and_notice(audit_page):
    """TC-AUD-02: Audit log page opens.

    Steps: log in, click "Audit log" in the sidebar.
    Expected: heading "Audit log" and the immutability notice are shown.
    """
    assert audit_page.heading() == "Audit log"
    notice = audit_page.text_of(al.IMMUTABILITY_NOTICE)
    assert "immutable" in notice.lower() and "append-only" in notice.lower()


@pytest.mark.audit
def test_audit_table_columns_and_state(audit_page):
    """TC-AUD-03: Audit Entries table structure.

    Steps: open the Audit log page and wait for the table to load.
    Expected: "Audit Entries" table with the 8 expected columns, rows or the empty
    text, and no error banner.
    """
    assert audit_page.is_visible(al.ENTRIES_TITLE), "'Audit Entries' title missing"
    assert audit_page.column_names() == EXPECTED_COLUMNS
    assert audit_page.row_count() > 0 or audit_page.is_empty()
    audit_page.assert_no_error_banner()


@pytest.mark.audit
def test_event_type_filter_and_reset(audit_page):
    """TC-AUD-04: Event Type filter applies and Reset clears every filter.

    Steps: type "order.placed" in Event Type, click "Apply filters"; then fill all
    filter fields and click "Reset".
    Expected: the filter is applied (in the URL), the table loads without an error
    banner, and after Reset every filter field is empty.
    """
    audit_page.set_filter("Event Type", "order.placed")
    audit_page.apply_filters()
    audit_page.wait_until(
        lambda d: "order.placed" in unquote(d.current_url), "Event Type filter not applied"
    )
    audit_page.assert_no_error_banner()

    for label, value in TEXT_FILTERS.items():
        audit_page.set_filter(label, value)
    # Digits only so the date works in both mm/dd and dd/mm locales.
    audit_page.set_filter("From Date (Colombo)", "01012026")
    audit_page.set_filter("To Date (Colombo)", "02012026")
    filled = {label: audit_page.filter_value(label) for label in al.FILTER_LABELS}
    assert all(filled.values()), f"Some filters were not filled: {filled}"

    audit_page.reset_filters()
    cleared = {label: audit_page.filter_value(label) for label in al.FILTER_LABELS}
    assert not any(cleared.values()), f"Reset left values: {cleared}"
    audit_page.assert_no_error_banner()


@pytest.mark.audit
@pytest.mark.negative
@pytest.mark.xfail(strict=True, reason="Known defect: non-UUID Aggregate ID is ignored and all entries are shown (see defects.md)")
def test_invalid_aggregate_id_is_handled(audit_page):
    """TC-AUD-05: Aggregate ID that is not a UUID.

    Steps: type "not-a-valid-uuid" in Aggregate ID, click "Apply filters".
    Expected: no results or a friendly message; never a raw error page.
    """
    audit_page.set_filter("Aggregate ID", "not-a-valid-uuid")
    audit_page.apply_filters()

    if audit_page.is_visible(al.ERROR_BANNER):
        banner = audit_page.text_of(al.ERROR_BANNER)
        assert "Unable to load audit entries" in banner
        assert not any(m in banner for m in RAW_ERROR_MARKERS), f"Raw error shown: {banner}"
    else:
        assert audit_page.is_empty(), "Expected no results for a non-UUID Aggregate ID"
    audit_page.assert_no_error_banner(allowed_test_ids=("audit-error-banner",))


@pytest.mark.audit
def test_open_and_close_entry_drawer(audit_page):
    """TC-AUD-06: Audit entry details drawer.

    Steps: click "Inspect" on the first audit entry, then close the drawer.
    Expected: the drawer shows the entry details and closes again.
    """
    if audit_page.row_count() == 0:
        pytest.skip("No audit entries in the test environment to inspect")
    audit_page.open_first_entry()
    assert not audit_page.is_visible(al.DRAWER_ERROR), audit_page.text_of(al.DRAWER_ERROR)
    assert audit_page.text_of(al.DRAWER_TITLE).startswith("Entry #")
    audit_page.close_drawer()


@pytest.mark.audit
def test_verify_integrity_shows_result(audit_page):
    """TC-AUD-07: Cryptographic chain verification.

    Steps: in "Cryptographic Chain Verification", click "Verify integrity".
    Expected: a result is shown (verified, or compromised with a reason) and no
    error banner.
    """
    if not audit_page.is_visible(al.VERIFY_PANEL):
        pytest.skip("Verify panel is only shown to Company Administrators")
    assert audit_page.is_visible(al.VERIFY_TITLE)
    result = audit_page.verify_integrity()
    assert result != al.VERIFY_ERROR, f"Verification error: {audit_page.text_of(al.VERIFY_ERROR)}"
    if result == al.VERIFY_FAILURE:
        assert "Reason:" in audit_page.text_of(al.VERIFY_FAILURE)
    else:
        assert "Chain integrity verified" in audit_page.text_of(al.VERIFY_SUCCESS)
    audit_page.assert_no_error_banner(allowed_test_ids=("verify-failure-banner",))
