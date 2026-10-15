<img src="web/public/logo.svg" alt="MediCare Plus" width="320">

# MediCare Plus

Multi-doctor, real-time clinic appointment platform. The product requirements are in [docs/PRD.md](docs/PRD.md).

**Current status:** PRD phases 1–4 are implemented: OTP sign-in, patient and family accounts, doctor registration with the mandatory declaration and Super Admin verification, doctor search and profiles, schedules, online and walk-in booking, the live queue with emergency priority and five-patient missed-turn requeue, real-time updates, in-app/Web Push/SMS notifications through an outbox, visit and doctor history, and Super Admin oversight with audit logs. Phase 5 groundwork is in place: a DLT-ready SMS gateway, product analytics (PRD §38), Free/Pro/Premium plan limits (§39), data-retention cleanup (§32.4), a k6 load test, and a backup/restore drill. What remains before the pilot is listed under [Before the pilot](#before-the-pilot).

## Structure

| Path | Contents |
|---|---|
| `src/SmartQueue.Api` | ASP.NET Core (.NET 10) Web API, EF Core, SQL Server |
| `tests/SmartQueue.Api.Tests` | xUnit unit and integration tests (real SQL Server LocalDB) |
| `web` | React 19 + TypeScript + Vite web app |
| `docs/PRD.md` | Product Requirements Document |
| `docs/operations` | Load-testing and backup/restore runbooks |
| `tests/load` | k6 load test for the PRD §35 targets |
| `scripts` | Operational scripts (restore drill) |

## Prerequisites

- .NET SDK 10
- Node.js 22 or later
- SQL Server LocalDB. The app uses a dedicated instance named `SmartQueue`. Create it once:

  ```sh
  sqllocaldb create SmartQueue -s
  ```

## Run locally

```sh
# Terminal 1 — API on http://localhost:5221 (applies migrations and seeds a Super Admin on startup)
dotnet run --project src/SmartQueue.Api --launch-profile http

# Terminal 2 — web app on http://localhost:5173 (proxies /api to the API)
cd web
npm install
npm run dev
```

In Development there is no SMS gateway: the one-time code is shown on the sign-in screen and written to the API log.

The development Super Admin signs in with mobile number **9000000001** (configured in `appsettings.Development.json` under `Bootstrap:SuperAdmins`). In development the authenticator-app step is off (`Auth:AdminMfaRequired` is `false`), so the phone code is enough.

## Test

```sh
dotnet test          # creates and deletes a temporary LocalDB database
cd web && npm run build && npm run lint
```

## Database migrations

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/SmartQueue.Api --output-dir Infrastructure/Migrations
```

## Configuration for non-development environments

Set these through environment variables or a secret store, never in committed files:

| Setting | Notes |
|---|---|
| `ConnectionStrings__Default` | SQL Server connection string |
| `Jwt__SigningKey` | At least 32 random characters; the app refuses to start without it |
| `Bootstrap__SuperAdmins__0__MobileNumber` | Seeds the first Super Admin account |
| `Auth__AdminMfaRequired` | `true` (default) asks Super Admins for an authenticator-app code after the phone code (PRD §27.4); `false` signs them in with the phone code alone |
| `Bootstrap__ApplyMigrationsOnStartup` | `false` in production; run migrations as a deployment step |
| `Sms__Provider` | `Http` to send real SMS; unset logs messages instead (development) |
| `Sms__Endpoint`, `Sms__ApiKey` | HTTPS gateway URL and bearer key. Each message is POSTed as `{ to, senderId, dltEntityId, dltTemplateId, type, message }` |
| `Sms__SenderId`, `Sms__DltEntityId`, `Sms__OtpTemplateId`, `Sms__NotificationTemplateId` | TRAI DLT registrations; the app refuses to start with `Provider=Http` if the endpoint, key, sender ID or OTP template is missing |
| `Plans__Free__StaffAccounts`, `Plans__Free__HistoryDays`, … | Plan limits (PRD §39, pending OQ-05). Super Admins change a doctor's plan on the doctor's page |
| `Retention__*` | Retention periods (PRD §32.4, pending OQ-03). The cleanup runs every 6 hours |
| `Push__*` | VAPID keys for Web Push |

`Otp:ExposeCodeInResponse` only takes effect in the Development environment.

## Operations

- Load test: [docs/operations/load-testing.md](docs/operations/load-testing.md)
- Backup, restore and the quarterly restore drill: [docs/operations/backup-restore.md](docs/operations/backup-restore.md)

## Before the pilot

These need decisions or access that the code cannot supply:

- **Legal and product decisions:** OQ-01 to OQ-06 in PRD §43 (verification method, final legal wording, retention periods, "Booked by" visibility, Free-tier limits, Super Admin break-glass approval). Retention and plan limits are configurable, so they can follow the decisions without code changes.
- **SMS:** register the sender ID and templates on the DLT platform, then set the `Sms__*` settings.
- **Pilot environment:** run the k6 load test and a restore drill there; verify push delivery on Android Chrome and an iOS home-screen PWA.
- **Not built yet:** automatic anonymization of appointments after 3 years (waits on OQ-03), a WebSocket load scenario for 5,000 concurrent connections, multi-clinic support for Pro/Premium, and Hindi/Marathi translations (needed for general availability).
