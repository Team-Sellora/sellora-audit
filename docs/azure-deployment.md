# Azure deployment setup

The GitHub Actions workflows build the Audit API into a Docker image, push it
to Docker Hub, and deploy that immutable image to Azure App Service. CI runs
for `dev` and `main` pushes and pull requests; staging deploys from `dev`; and
production deploys from `main`.

## One-time Azure setup

1. Create a Linux Azure App Service configured for a classic single Docker Hub
   container (do not enable sidecar support).
   Set its container port to `8080`, and leave its startup command empty.
2. Create a `staging` deployment slot on the App Service if staging and
   production use the same App Service.
3. Make sure both production and staging expose HTTPS and that
   `https://<host>/health` can be reached publicly by GitHub-hosted runners.
4. Download the publish profile for the staging slot and for production. Treat
   each profile as a credential and never add it to the repository.
5. Add the application settings needed by future Audit features in Azure App
   Service configuration (for example, `ConnectionStrings__Default` and Kafka
   settings). The current service skeleton needs no settings to start.

## GitHub configuration

Create GitHub Environments named `staging` and `production`. Configure an
approval rule on `production` before enabling production deployments.

Add these environment-scoped values:

| Environment | Type | Name | Value |
| --- | --- | --- | --- |
| repository | variable | `DOCKERHUB_USERNAME` | `naveentharuka` |
| repository | secret | `DOCKERHUB_TOKEN` | Docker Hub read/write access token |
| staging | variable | `AZURE_STAGING_APP_NAME` | Azure App Service name |
| staging | variable | `STAGING_BASE_URL` | Public staging URL, without a trailing slash |
| staging | secret | `AZURE_STAGING_PUBLISH_PROFILE` | Publish profile XML for the staging slot |
| production | variable | `AZURE_PRODUCTION_APP_NAME` | Azure App Service name |
| production | variable | `AZURE_PRODUCTION_BASE_URL` | Public production URL, without a trailing slash |
| production | secret | `AZURE_PRODUCTION_PUBLISH_PROFILE` | Publish profile XML for production |

GitHub environment secrets are intentionally used so the publish credentials
are available only to the matching deployment job.

Add `SONAR_TOKEN` as a repository secret and create the
`Team-Sellora_sellora-audit` project in the `team-sellora` SonarCloud
organization. CI uses this token to publish test coverage and quality analysis,
matching the other Sellora services.

## Release flow

1. Open a pull request to `dev`; CI must pass.
2. Merge to `dev`; the staging workflow publishes the API and checks
   `/health`.
3. Verify the staging service, then open and merge a pull request from `dev`
   to `main`.
4. The production environment approval gate must be approved before the
   production workflow deploys and checks `/health`.
