import os

from selenium.webdriver.common.by import By

from pages.base_page import SIDEBAR, BasePage

USERNAME_INPUT = (By.ID, "usernameUserInput")
PASSWORD_INPUT = (By.ID, "password")
SUBMIT_BUTTON = (By.CSS_SELECTOR, "button[type='submit']")
APP_SIGN_IN_BUTTON = (
    By.XPATH,
    "//button[normalize-space()='Sign in' or normalize-space()='Try again']",
)
# Identity server consent screen; exact markup not in the web source (guessed).
CONSENT_BUTTON = (
    By.XPATH,
    "//button[normalize-space()='Allow' or normalize-space()='Approve' or normalize-space()='Continue']"
    " | //input[@type='submit' and (@value='Allow' or @value='Approve' or @value='Continue')]",
)


def credentials():
    user = os.getenv("TEST_USER")
    password = os.getenv("TEST_PASSWORD")
    if not user or not password:
        raise RuntimeError("TEST_USER and TEST_PASSWORD environment variables must be set")
    return user, password


class LoginPage(BasePage):
    def __init__(self, driver, base_url):
        super().__init__(driver)
        self.base_url = base_url
        self.login_timeout = int(os.getenv("LOGIN_TIMEOUT", "60"))

    def _step(self, name, condition):
        return self.wait_until(condition, f"Login step '{name}' timed out", self.login_timeout)

    def _first_visible(self, *locators):
        def _check(_drv):
            for locator in locators:
                if self.is_visible(locator):
                    return locator
            return False

        return _check

    def _login_once(self, user, password):
        self.driver.get(self.base_url)
        found = self._step(
            "open login form", self._first_visible(SIDEBAR, USERNAME_INPUT, APP_SIGN_IN_BUTTON)
        )
        if found == SIDEBAR:
            return
        if found == APP_SIGN_IN_BUTTON:
            self.click(APP_SIGN_IN_BUTTON)
            self._step("identity server form", self._first_visible(USERNAME_INPUT))

        self.type_into(USERNAME_INPUT, user)
        self.type_into(PASSWORD_INPUT, password)
        self.click(SUBMIT_BUTTON)

        def _after_submit(drv):
            if self.is_visible(SIDEBAR):
                return SIDEBAR
            if not self.is_visible(USERNAME_INPUT) and self.is_visible(CONSENT_BUTTON):
                return CONSENT_BUTTON
            return False

        if self._step("sidebar or consent after submit", _after_submit) == CONSENT_BUTTON:
            self.click(CONSENT_BUTTON)
            self._step("sidebar after consent", self._first_visible(SIDEBAR))

    def login(self):
        user, password = credentials()
        try:
            self._login_once(user, password)
        except AssertionError:
            self._login_once(user, password)
