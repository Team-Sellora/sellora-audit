#!/usr/bin/env bash
set -euo pipefail

# US-E7-1-D2: migrations run as audit_owner, never as the runtime role, and
# the app never migrates at startup. Afterwards the roles script is run again
# so the runtime role's grants cover any new table.
#
#   ConnectionStrings__Owner   owner connection (required outside local dev)
#   ADMIN_CONNECTION           optional psql URI of an administrator, to re-apply grants
DEFAULT_OWNER="Host=localhost;Port=5438;Database=audit_db;Username=audit_owner;Password=audit_owner_dev_pw"
OWNER_CONNECTION="$(printenv ConnectionStrings__Owner || true)"
OWNER_CONNECTION="${OWNER_CONNECTION:-${DEFAULT_OWNER}}"

dotnet tool restore

env "ConnectionStrings__Owner=${OWNER_CONNECTION}" dotnet ef database update \
  --project src/Sellora.AuditService.Infrastructure/Sellora.AuditService.Infrastructure.csproj \
  --startup-project src/Sellora.AuditService.Api/Sellora.AuditService.Api.csproj

if [ -n "${ADMIN_CONNECTION:-}" ]; then
  psql "${ADMIN_CONNECTION}" -v ON_ERROR_STOP=1 \
    -v owner_password="${AUDIT_OWNER_PASSWORD:?}" \
    -v app_password="${AUDIT_APP_PASSWORD:?}" \
    -f scripts/create-db-roles.sql
fi

echo "Migrations applied successfully"
