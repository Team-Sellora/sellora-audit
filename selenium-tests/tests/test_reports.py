from datetime import date

import pytest

from pages import reports_page as rp
from pages.login_page import LoginPage
from pages.reports_page import ReportsPage


@pytest.fixture
def reports_page(driver, base_url):
    LoginPage(driver, base_url).login()
    return ReportsPage(driver).open()


def as_dates(pair):
    return tuple(date.fromisoformat(v) for v in pair)


@pytest.mark.reports
def test_reports_page_opens_with_presets_and_dates(reports_page):
    """TC-AUD-08: Reports page opens.

    Steps: log in, click "Reports" in the sidebar.
    Expected: heading "Reporting dashboard", the presets "This month", "Last month",
    "Last 90 days", the From date and To date fields, the Summary/Breakdown select
    and the Export CSV / Export PDF buttons are shown.
    """
    assert reports_page.heading() == "Reporting dashboard"
    for name in rp.PRESETS:
        assert reports_page.is_visible(rp.preset_button(name)), f"Preset '{name}' missing"
    start, end = as_dates(reports_page.date_range())
    assert start <= end
    assert set(reports_page.report_mode_options()) == {"Breakdown", "Summary"}
    assert reports_page.is_visible(rp.EXPORT_CSV)
    assert reports_page.is_visible(rp.EXPORT_PDF)
    reports_page.assert_no_error_banner()


@pytest.mark.reports
def test_presets_update_date_range(reports_page):
    """TC-AUD-09: Date presets.

    Steps: click "Last month", then "Last 90 days".
    Expected: the date fields change each time (Last month starts on day 1, Last 90
    days spans about 90 days) and the summary or the empty state is shown without
    an error banner.
    """
    this_month = as_dates(reports_page.date_range())

    reports_page.choose_preset("Last month")
    start, end = as_dates(reports_page.date_range())
    assert (start, end) != this_month
    assert start.day == 1 and start <= end and start < this_month[0]
    reports_page.assert_no_error_banner()

    reports_page.choose_preset("Last 90 days")
    start, end = as_dates(reports_page.date_range())
    assert 85 <= (end - start).days <= 92, f"Unexpected range {start}..{end}"
    assert reports_page.has_summary_empty_state() or reports_page.is_visible(rp.BREAKDOWN_SECTION)
    reports_page.assert_no_error_banner()


@pytest.mark.reports
def test_compare_previous_period_toggle(reports_page):
    """TC-AUD-10: Compare previous period.

    Steps: switch "Compare previous period" on, then off.
    Expected: when on, the comparison date fields appear; when off, they are hidden;
    no error banner either way.
    """
    assert not reports_page.compare_enabled()
    reports_page.toggle_compare()
    assert reports_page.compare_enabled()
    assert reports_page.is_visible(rp.COMPARE_FROM)
    reports_page.assert_no_error_banner()

    reports_page.toggle_compare()
    assert not reports_page.compare_enabled()
    assert not reports_page.is_visible(rp.COMPARE_FROM)
    reports_page.assert_no_error_banner()


@pytest.mark.reports
def test_breakdown_tabs(reports_page):
    """TC-AUD-11: Sales Breakdown tabs.

    Steps: choose "Last 90 days", then click the tabs Rep, Territory, Agency, Product.
    Expected: each tab is selected and shows its table rows or
    "No confirmed sales for this breakdown", without an error banner.
    """
    reports_page.choose_preset("Last 90 days")
    if reports_page.has_summary_empty_state():
        pytest.skip("No confirmed sales in the last 90 days, so the breakdown is not rendered")
    assert reports_page.is_visible(rp.BREAKDOWN_TITLE)
    for name in rp.TAB_NAMES:
        reports_page.select_tab(name)
        assert reports_page.wait_for_breakdown() in ("rows", "empty")
        reports_page.assert_no_error_banner()
