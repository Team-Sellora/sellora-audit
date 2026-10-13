# Sellora Audit Service

Epic E7: audit log, reporting and exports for Sellora. This branch holds only the
service skeleton (a host with a health endpoint) so the build and deployment
pipeline can be set up before feature work starts.

Layout, same as sellora-order: `src/` holds `Domain`, `Application`,
`Infrastructure` and `Api`; `tests/` holds `Sellora.AuditService.Tests`. .NET 8.

## Build and test

```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
```

The tests need no database or Docker.

## Run

```bash
dotnet run --project src/Sellora.AuditService.Api   # http://localhost:5208/health
```

With Docker (container port 8080):

```bash
docker build -t sellora-audit .
docker run --rm -p 8080:8080 sellora-audit           # http://localhost:8080/health
```

`GET /health` returns `200` with `{"status":"Healthy"}`. No environment variables are required.

`docker compose up -d audit-db` starts a local PostgreSQL 16 on port 5438
(copy `.env.example` to `.env` first). The skeleton does not use it yet.

## Planned stories (not implemented yet)

- CSP-250: immutable audit log
- CSP-252: reporting
- CSP-253: exports
