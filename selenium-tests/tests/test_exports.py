import pytest

from pages import my_exports_panel as mx
from pages.login_page import LoginPage
from pages.my_exports_panel import MyExportsPanel
from pages.reports_page import ReportsPage

EXPORT_WAIT = 60


@pytest.fixture
def reports_page(driver, base_url):
    LoginPage(driver, base_url).login()
    return ReportsPage(driver).open()


@pytest.mark.exports
def test_export_csv_appears_in_my_exports(reports_page, driver):
    """TC-AUD-12: Export CSV from the Reports page.

    Steps: open "My exports" (Show), click "Export CSV" with mode "Breakdown".
    Expected: a new "Sales breakdown" CSV export appears in My exports with status
    Queued, Running, Ready or Failed within 60 seconds.
    """
    panel = MyExportsPanel(driver)
    assert panel.is_visible(mx.TITLE), "'My exports' panel missing"
    panel.show()
    assert not panel.has_error(), f"My exports error: {panel.text_of(mx.ERROR)}"
    before = panel.count_matching("Sales breakdown", "CSV")

    reports_page.export_csv()

    def _new_export(_drv):
        rows = [r for r in panel.rows() if r[0] == "Sales breakdown" and r[1] == "CSV"]
        if len(rows) > before and any(r[2] in mx.STATUSES for r in rows):
            return rows
        return False

    panel.wait_until(_new_export, "New CSV export not listed in My exports", timeout=EXPORT_WAIT)
    reports_page.assert_no_error_banner()
