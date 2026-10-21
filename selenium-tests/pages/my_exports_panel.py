from selenium.webdriver.common.by import By

from pages.base_page import BasePage

PANEL = (By.CSS_SELECTOR, "[data-testid='my-exports-panel']")
TITLE = (By.XPATH, "//*[@data-testid='my-exports-panel']//h2[normalize-space()='My exports']")
TOGGLE = (By.CSS_SELECTOR, "[data-testid='my-exports-panel'] button[aria-expanded]")
SKELETON = (By.CSS_SELECTOR, "[data-testid='my-exports-skeleton']")
EMPTY = (By.XPATH, "//*[@data-testid='my-exports-panel']//p[normalize-space()='No exports yet.']")
ERROR = (By.CSS_SELECTOR, "[data-testid='my-exports-panel'] [role='alert']")
ROWS = (By.CSS_SELECTOR, "[data-testid='my-exports-panel'] table tbody tr")
STATUSES = ("Queued", "Running", "Ready", "Failed")


class MyExportsPanel(BasePage):
    def is_open(self):
        return self.attr_of(TOGGLE, "aria-expanded") == "true"

    def show(self):
        if not self.is_open():
            self.click(TOGGLE)
            self.wait_until(lambda d: self.is_open(), "My exports panel did not open")
        self.wait_loaded()

    def wait_loaded(self):
        def _loaded(_drv):
            if self.is_visible(SKELETON):
                return False
            return (
                self.driver.find_elements(*ROWS) or self.is_visible(EMPTY) or self.is_visible(ERROR)
            )

        self.wait_until(_loaded, "My exports list did not load")

    def has_error(self):
        return self.is_visible(ERROR)

    def rows(self):
        """Each export row as (report, format, status)."""

        def _collect():
            result = []
            for row in self.driver.find_elements(*ROWS):
                cells = [c.text.strip() for c in row.find_elements(By.TAG_NAME, "td")]
                if len(cells) >= 3:
                    report = cells[0].splitlines()[0] if cells[0] else ""
                    result.append((report, cells[1].upper(), cells[2]))
            return result

        return self.read(_collect)

    def count_matching(self, report, fmt):
        return sum(1 for r in self.rows() if r[0] == report and r[1] == fmt)
