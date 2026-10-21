import base64
import os
from pathlib import Path

import pytest
from selenium import webdriver

try:
    import pytest_html
except ImportError:
    pytest_html = None

ROOT = Path(__file__).parent
REPORTS_DIR = ROOT / "reports"
SCREENSHOTS_DIR = REPORTS_DIR / "screenshots"
DOWNLOADS_DIR = REPORTS_DIR / "downloads"


@pytest.fixture(scope="session")
def base_url():
    return os.getenv("BASE_URL", "http://test.selloralk.tech").rstrip("/")


@pytest.fixture
def driver():
    options = webdriver.ChromeOptions()
    if os.getenv("HEADLESS", "1") != "0":
        options.add_argument("--headless=new")
    for arg in (
        "--no-sandbox",
        "--disable-dev-shm-usage",
        "--window-size=1920,1080",
        "--ignore-certificate-errors",
    ):
        options.add_argument(arg)
    options.set_capability("acceptInsecureCerts", True)
    DOWNLOADS_DIR.mkdir(parents=True, exist_ok=True)
    options.add_experimental_option(
        "prefs",
        {
            "download.default_directory": str(DOWNLOADS_DIR),
            "download.prompt_for_download": False,
        },
    )
    drv = webdriver.Chrome(options=options)
    yield drv
    drv.quit()


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item, call):
    outcome = yield
    report = outcome.get_result()
    # One screenshot per test: after the test body, or after a failed/skipped setup.
    if report.when == "call" or (report.when == "setup" and not report.passed):
        drv = item.funcargs.get("driver")
        if drv is None:
            return
        SCREENSHOTS_DIR.mkdir(parents=True, exist_ok=True)
        path = SCREENSHOTS_DIR / f"{item.name}-{report.outcome}.png"
        try:
            drv.save_screenshot(str(path))
        except Exception:
            return
        if pytest_html is not None:
            extras = getattr(report, "extras", [])
            encoded = base64.b64encode(path.read_bytes()).decode("ascii")
            extras.append(pytest_html.extras.png(encoded))
            report.extras = extras
