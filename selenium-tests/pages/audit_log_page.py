from selenium.webdriver.common.by import By

from pages.base_page import BasePage


def by_label(text):
    return (By.XPATH, f"//input[@id=//label[normalize-space()='{text}']/@for]")


HEADING_TEXT = "Audit log"
IMMUTABILITY_NOTICE = (By.CSS_SELECTOR, "[data-testid='audit-immutability-notice']")
VERIFY_PANEL = (By.CSS_SELECTOR, "[data-testid='audit-verify-panel']")
VERIFY_TITLE = (By.XPATH, "//h3[normalize-space()='Cryptographic Chain Verification']")
VERIFY_BUTTON = (By.CSS_SELECTOR, "[data-testid='verify-integrity-button']")
VERIFY_SUCCESS = (By.CSS_SELECTOR, "[data-testid='verify-success-banner']")
VERIFY_FAILURE = (By.CSS_SELECTOR, "[data-testid='verify-failure-banner']")
VERIFY_ERROR = (
    By.CSS_SELECTOR,
    "[data-testid='audit-verify-panel'] [role='alert']:not([data-testid])",
)

FILTER_FORM = (By.CSS_SELECTOR, "[data-testid='audit-filter-form']")
FILTER_LABELS = (
    "Order Reference",
    "Event Type",
    "Aggregate Type",
    "Aggregate ID",
    "Actor User ID",
    "From Date (Colombo)",
    "To Date (Colombo)",
)
APPLY_BUTTON = (
    By.XPATH,
    "//form[@data-testid='audit-filter-form']//button[normalize-space()='Apply filters']",
)
RESET_BUTTON = (
    By.XPATH,
    "//form[@data-testid='audit-filter-form']//button[normalize-space()='Reset']",
)

ENTRIES_TITLE = (By.XPATH, "//h3[normalize-space()='Audit Entries']")
TABLE = (By.CSS_SELECTOR, "[data-testid='audit-table']")
HEADER_CELLS = (By.CSS_SELECTOR, "[data-testid='audit-table'] thead th")
ROWS = (By.CSS_SELECTOR, "[data-testid='audit-table'] tbody tr[data-testid^='audit-row-']")
LOADING_ROW = (By.XPATH, "//td[contains(normalize-space(), 'Loading audit entries')]")
EMPTY_ROW = (
    By.XPATH,
    "//td[normalize-space()='No audit entries found matching the specified filters.']",
)
ERROR_BANNER = (By.CSS_SELECTOR, "[data-testid='audit-error-banner']")
FIRST_INSPECT = (
    By.XPATH,
    "(//table[@data-testid='audit-table']//tr[starts-with(@data-testid, 'audit-row-')]"
    "//button[normalize-space()='Inspect'])[1]",
)

DRAWER = (By.CSS_SELECTOR, "[data-testid='audit-entry-drawer']")
# Radix sheet title renders as h2 (guessed from the shadcn sheet).
DRAWER_TITLE = (By.CSS_SELECTOR, "[data-testid='audit-entry-drawer'] h2")
DRAWER_METADATA = (
    By.XPATH,
    "//*[@data-testid='audit-entry-drawer']//h4[normalize-space()='Metadata']",
)
DRAWER_ERROR = (By.CSS_SELECTOR, "[data-testid='audit-entry-drawer'] [role='alert']")
DRAWER_CLOSE = (
    By.XPATH,
    "//*[@data-testid='audit-entry-drawer']//button[normalize-space()='Close']",
)


class AuditLogPage(BasePage):
    def open(self):
        self.open_from_sidebar("Audit log")
        self.wait_until(lambda d: self.heading() == HEADING_TEXT, "Audit log heading not shown")
        self.wait_for_table()
        return self

    def wait_for_table(self):
        def _loaded(_drv):
            if self.is_visible(LOADING_ROW):
                return False
            return (
                self.driver.find_elements(*ROWS)
                or self.is_visible(EMPTY_ROW)
                or self.is_visible(ERROR_BANNER)
            )

        self.wait_until(_loaded, "Audit Entries table did not finish loading")

    def column_names(self):
        return self.read(
            lambda: [th.text.strip().upper() for th in self.driver.find_elements(*HEADER_CELLS)]
        )

    def row_count(self):
        return len(self.driver.find_elements(*ROWS))

    def is_empty(self):
        return self.is_visible(EMPTY_ROW)

    def set_filter(self, label, value):
        self.type_into(by_label(label), value)

    def filter_value(self, label):
        return self.attr_of(by_label(label), "value") or ""

    def apply_filters(self):
        self.click(APPLY_BUTTON)
        self.wait_for_table()

    def reset_filters(self):
        self.click(RESET_BUTTON)
        self.wait_for_table()

    def open_first_entry(self):
        self.click(FIRST_INSPECT)
        self.visible(DRAWER)

        def _drawer_ready(_drv):
            return self.is_visible(DRAWER_METADATA) or self.is_visible(DRAWER_ERROR)

        self.wait_until(_drawer_ready, "Audit entry drawer did not load")

    def close_drawer(self):
        self.click(DRAWER_CLOSE)
        self.wait_until(lambda d: not self.is_visible(DRAWER), "Audit entry drawer did not close")

    def verify_integrity(self):
        self.click(VERIFY_BUTTON)

        def _result(_drv):
            for locator in (VERIFY_SUCCESS, VERIFY_FAILURE, VERIFY_ERROR):
                if self.is_visible(locator):
                    return locator
            return False

        return self.wait_until(_result, "No verification result shown", timeout=90)
