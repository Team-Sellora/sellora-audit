# Selenium UI tests: Audit

UI tests for the Audit log, Reports and Exports pages of the Sellora web app.
Python, Selenium 4 (Selenium Manager downloads the Chrome driver), pytest and pytest-html, Page Object Model.

## Layout

- `pages/` page objects (login, audit log, reports, My exports panel)
- `tests/` test files, IDs TC-AUD-01 to TC-AUD-12
- `test-cases.md` test case table
- `reports/` HTML report, JUnit XML and screenshots (created by the run, not committed)

## Setup

Needs Google Chrome and Python 3.12+.

```powershell
cd selenium-tests
py -3.12 -m venv .venv
.\.venv\Scripts\python -m pip install -r requirements.txt
```

## Environment variables

| Name | Default | Purpose |
|------|---------|---------|
| `BASE_URL` | `http://test.selloralk.tech` | Site under test |
| `TEST_USER` | (required) | Login user (CompanyAdmin for full coverage) |
| `TEST_PASSWORD` | (required) | Login password |
| `HEADLESS` | `1` | `0` shows the browser |
| `LOGIN_TIMEOUT` | `60` | Seconds for each login step |

## Run locally

```powershell
$env:TEST_USER = "<user>"
$env:TEST_PASSWORD = Read-Host "Password"
.\.venv\Scripts\python -m pytest --junitxml=reports/junit.xml
```

Open `reports/report.html` for the results. Screenshots are in `reports/screenshots/`.

## Run in the pipeline

`.github/workflows/selenium.yml` runs on every push to `test/selenium-audit` and can be started by hand (workflow_dispatch).
It needs the repository secrets `SELLORA_TEST_USER` and `SELLORA_TEST_PASSWORD`.
The report is uploaded as the artifact `selenium-audit-report`.

## Notes

- Tests are read-only. The only write is one queued CSV export (TC-AUD-12).
- After login, pages are opened from the sidebar because the login token is kept only in memory.
