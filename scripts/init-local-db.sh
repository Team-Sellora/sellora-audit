#!/usr/bin/env bash
# Runs once when the local audit-db container is first created.
set -euo pipefail

psql -v ON_ERROR_STOP=1 \
  -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  -v owner_password="$AUDIT_OWNER_PASSWORD" \
  -v app_password="$AUDIT_APP_PASSWORD" \
  -f /scripts/create-db-roles.sql
