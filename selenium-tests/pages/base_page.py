from selenium.common.exceptions import StaleElementReferenceException, TimeoutException
from selenium.webdriver.common.by import By
from selenium.webdriver.support import expected_conditions as EC
from selenium.webdriver.support.ui import WebDriverWait

DEFAULT_TIMEOUT = 30
STALE_RETRIES = 5

SIDEBAR = (By.CSS_SELECTOR, "nav[aria-label='Main navigation']")
PAGE_HEADING = (By.CSS_SELECTOR, ".page-heading h1")
ALERTS = (By.CSS_SELECTOR, "[role='alert']")
# Default TanStack Router error screen text.
RAW_ERROR_TEXTS = ("Something went wrong", "Unexpected Application Error")


def sidebar_link(label):
    return (By.CSS_SELECTOR, f"nav[aria-label='Main navigation'] a[title='{label}']")


class BasePage:
    def __init__(self, driver, timeout=DEFAULT_TIMEOUT):
        self.driver = driver
        self.timeout = timeout
        self.wait = self.make_wait(timeout)

    def make_wait(self, timeout):
        return WebDriverWait(
            self.driver, timeout, ignored_exceptions=(StaleElementReferenceException,)
        )

    def read(self, fn):
        """Run fn and retry when the element it uses goes stale."""
        for attempt in range(STALE_RETRIES):
            try:
                return fn()
            except StaleElementReferenceException:
                if attempt == STALE_RETRIES - 1:
                    raise
        return None

    def find(self, locator):
        return self.wait.until(EC.presence_of_element_located(locator))

    def visible(self, locator, timeout=None):
        wait = self.make_wait(timeout) if timeout else self.wait
        return wait.until(EC.visibility_of_element_located(locator))

    def text_of(self, locator):
        return self.read(lambda: self.visible(locator).text.strip())

    def attr_of(self, locator, name):
        return self.read(lambda: self.find(locator).get_attribute(name))

    def click(self, locator):
        self.read(lambda: self.wait.until(EC.element_to_be_clickable(locator)).click())

    def type_into(self, locator, value):
        def _type():
            el = self.visible(locator)
            el.clear()
            el.send_keys(value)

        self.read(_type)

    def is_visible(self, locator):
        def _check():
            return any(el.is_displayed() for el in self.driver.find_elements(*locator))

        try:
            return self.read(_check)
        except StaleElementReferenceException:
            return False

    def wait_until(self, condition, message, timeout=None):
        """condition(driver) -> truthy; stale errors count as False."""

        def _safe(drv):
            try:
                return condition(drv)
            except StaleElementReferenceException:
                return False

        wait = self.make_wait(timeout) if timeout else self.wait
        try:
            return wait.until(_safe)
        except TimeoutException as exc:
            raise AssertionError(
                f"{message} (waited {timeout or self.timeout}s, url={self.driver.current_url})"
            ) from exc

    def heading(self):
        return self.text_of(PAGE_HEADING)

    def open_from_sidebar(self, label):
        self.click(sidebar_link(label))

    def sidebar_has(self, label):
        return self.is_visible(sidebar_link(label))

    def error_messages(self, allowed_test_ids=()):
        """Texts of visible alert banners, ignoring allowed data-testid or id values."""

        def _collect():
            texts = []
            for el in self.driver.find_elements(*ALERTS):
                if not el.is_displayed():
                    continue
                keys = (el.get_attribute("data-testid"), el.get_attribute("id"))
                if any(k and k in allowed_test_ids for k in keys):
                    continue
                texts.append(el.text.strip())
            return texts

        messages = self.read(_collect)
        body = self.read(lambda: self.driver.find_element(By.TAG_NAME, "body").text)
        messages += [t for t in RAW_ERROR_TEXTS if t in body]
        return messages

    def assert_no_error_banner(self, allowed_test_ids=()):
        messages = self.error_messages(allowed_test_ids)
        assert not messages, f"Error banner shown: {messages}"
