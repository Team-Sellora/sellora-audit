import pytest

from pages.base_page import BasePage
from pages.login_page import LoginPage


@pytest.mark.smoke
def test_login_shows_audit_and_reports_in_sidebar(driver, base_url):
    """TC-AUD-01: Login succeeds.

    Steps: open BASE_URL, sign in with TEST_USER / TEST_PASSWORD.
    Expected: the app sidebar is shown with "Audit log" and "Reports".
    """
    LoginPage(driver, base_url).login()
    page = BasePage(driver)
    assert page.sidebar_has("Audit log"), "Sidebar has no 'Audit log' item"
    assert page.sidebar_has("Reports"), "Sidebar has no 'Reports' item"
