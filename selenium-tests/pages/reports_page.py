from selenium.webdriver.common.by import By
from selenium.webdriver.support.ui import Select

from pages.base_page import BasePage

HEADING_TEXT = "Reporting dashboard"
PRESETS = ("This month", "Last month", "Last 90 days")
FROM_DATE = (By.XPATH, "//input[@id=//label[normalize-space()='From date']/@for]")
TO_DATE = (By.XPATH, "//input[@id=//label[normalize-space()='To date']/@for]")
COMPARE_SWITCH = (
    By.XPATH,
    "//button[@role='switch' and @id=//label[normalize-space()='Compare previous period']/@for]",
)
COMPARE_FROM = (
    By.XPATH,
    "//input[@id=//label[normalize-space()='Comparison from date']/@for]",
)
REPORT_MODE_SELECT = (By.CSS_SELECTOR, "select[aria-label='Select report content to export']")
EXPORT_CSV = (By.CSS_SELECTOR, "button[aria-label='Export report as CSV']")
EXPORT_PDF = (By.CSS_SELECTOR, "button[aria-label='Export report as PDF']")

SUMMARY_EMPTY = (By.ID, "reports-empty-state")
BREAKDOWN_SECTION = (By.ID, "section-breakdown")
BREAKDOWN_TITLE = (By.XPATH, "//h2[normalize-space()='Sales Breakdown']")
BREAKDOWN_TABLE = (By.CSS_SELECTOR, "#section-breakdown table")
BREAKDOWN_ROWS = (By.CSS_SELECTOR, "#section-breakdown table tbody tr")
BREAKDOWN_EMPTY = (By.XPATH, "//td[normalize-space()='No confirmed sales for this breakdown']")
TAB_NAMES = ("Rep", "Territory", "Agency", "Product")
# Reports 503 notice is a warning that names may be missing, not a failure.
ALLOWED_ALERTS = ("reports-503-notice",)


def preset_button(name):
    return (By.XPATH, f"//button[normalize-space()='{name}']")


def tab(name):
    return (By.XPATH, f"//*[@id='section-breakdown']//button[@role='tab' and normalize-space()='{name}']")


class ReportsPage(BasePage):
    def open(self):
        self.open_from_sidebar("Reports")
        self.wait_until(lambda d: self.heading() == HEADING_TEXT, "Reports heading not shown")
        self.wait_for_content()
        return self

    def wait_for_content(self):
        def _loaded(_drv):
            return (
                self.is_visible(SUMMARY_EMPTY)
                or self.is_visible(BREAKDOWN_SECTION)
                or bool(self.error_messages(ALLOWED_ALERTS))
            )

        self.wait_until(_loaded, "Reports summary did not load")

    def assert_no_error_banner(self, allowed_test_ids=ALLOWED_ALERTS):
        super().assert_no_error_banner(allowed_test_ids)

    def date_range(self):
        return self.attr_of(FROM_DATE, "value"), self.attr_of(TO_DATE, "value")

    def choose_preset(self, name):
        before = self.date_range()
        self.click(preset_button(name))
        if name != "This month":
            self.wait_until(lambda d: self.date_range() != before, f"'{name}' did not change dates")
        self.wait_for_content()

    def compare_enabled(self):
        return self.attr_of(COMPARE_SWITCH, "aria-checked") == "true"

    def toggle_compare(self):
        expected = not self.compare_enabled()
        self.click(COMPARE_SWITCH)
        self.wait_until(
            lambda d: self.compare_enabled() == expected, "Compare toggle did not change"
        )
        self.wait_for_content()

    def has_summary_empty_state(self):
        return self.is_visible(SUMMARY_EMPTY)

    def report_mode_options(self):
        return self.read(lambda: [o.text for o in Select(self.find(REPORT_MODE_SELECT)).options])

    def select_tab(self, name):
        self.click(tab(name))
        self.wait_until(
            lambda d: self.attr_of(tab(name), "aria-selected") == "true",
            f"Tab '{name}' not selected",
        )
        self.wait_for_breakdown()

    def wait_for_breakdown(self):
        def _loaded(_drv):
            if self.is_visible(BREAKDOWN_EMPTY):
                return "empty"
            rows = self.driver.find_elements(*BREAKDOWN_ROWS)
            # Skeleton rows have no text.
            if rows and all(r.text.strip() for r in rows):
                return "rows"
            return False

        return self.wait_until(_loaded, "Sales Breakdown table did not load")

    def export_csv(self):
        self.click(EXPORT_CSV)
