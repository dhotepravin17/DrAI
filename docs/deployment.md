# Deploying Valmedo - Appointment

This guide covers testing deployments (free), production options with approximate costs, and step-by-step
instructions for the scripted setup: a budget Linux server running Docker Compose.

> Prices are approximate (late 2026) and change often. Check the provider's pricing page before you buy.

**Contents**
1. [How the app runs in production](#1-how-the-app-runs-in-production)
2. [Which option should I choose?](#2-which-option-should-i-choose)
3. [Testing for free](#3-testing-for-free)
4. [Production options and costs](#4-production-options-and-costs)
5. [Step by step: budget VPS (scripted)](#5-step-by-step-budget-vps-scripted)
6. [Step by step: Azure managed (Central India)](#6-step-by-step-azure-managed-central-india)
7. [Configuration reference](#7-configuration-reference)
8. [Updates, backups and monitoring](#8-updates-backups-and-monitoring)
9. [Go-live checklist](#9-go-live-checklist)
10. [Android app](#10-android-app)

---

## 1. How the app runs in production

```
 Phones / browsers ──HTTPS──▶  Reverse proxy (Caddy, App Service, Cloudflare)
                                    │  automatic certificate, WebSockets
                                    ▼
                          Valmedo container (port 8080)
                          ├─ web app (React, served from wwwroot)
                          ├─ REST API  /api/v1/*
                          ├─ live updates  /hubs/queue (SignalR)
                          └─ background workers: notifications, data retention
                                    │
                                    ▼
                          SQL Server (Express is enough for a pilot)
```

Things to know before choosing a host:

| Requirement | Why |
|---|---|
| **One container serves both the web app and the API** | The web app calls `/api` and `/hubs` on its own address. The Docker image copies the built web app into the API. |
| **SQL Server** (any edition, including free Express) | The app uses SQL Server features. PostgreSQL/MySQL hosting will not work. |
| **x86-64 server** | SQL Server containers do not run on ARM (for example Oracle Ampere, AWS Graviton). |
| **HTTPS** | Required for sign-in security, Web Push alerts and installing the app on phones. |
| **WebSockets** | Live appointment updates. Supported by every option below. |
| **One app instance** | The notification and cleanup workers assume a single instance (two would send duplicate SMS). Scale up (bigger server), not out. |
| **Always on** | Background workers send turn alerts. Hosts that sleep when idle delay alerts. |
| **At least 2 GB RAM (4 GB recommended)** if SQL Server runs on the same machine | SQL Server needs about 1.5 GB. |

The image is built by GitHub Actions on every push and published to:

- `ghcr.io/dhotepravin17/drai` — the app
- `ghcr.io/dhotepravin17/drai-migrator` — applies database migrations, then exits

Tags: `sha-<commit>`, the branch name (for example `worktree-review-fixes`), `latest` (default branch) and `vX.Y.Z`
for release tags. The packages are private by default; make them public in GitHub → Packages → Package settings, or
log in on the server with `docker login ghcr.io` (a personal access token with `read:packages`).

---

## 2. Which option should I choose?

| Stage | Recommendation | Cost |
|---|---|---|
| **Show it to someone today** | Your PC + Cloudflare quick tunnel ([3A](#3a-from-your-pc-with-a-cloudflare-tunnel-free-10-minutes)) | Free |
| **Test with real users for a few weeks** | Budget VPS with new-account credits ([3B](#3b-a-real-server-on-free-credits)) | Free for 1–3 months |
| **Pilot (3–5 doctors, PRD §41)** | Budget VPS in India, 4 GB RAM ([section 5](#5-step-by-step-budget-vps-scripted)) | ~$20–25/month |
| **Lowest possible cost** | Hetzner 4 GB VPS ([section 4](#4-production-options-and-costs)) | ~€5–6/month |
| **Production at scale, least maintenance, PRD-preferred region** | Azure managed in Central India ([section 6](#6-step-by-step-azure-managed-central-india)) | ~$30–90/month |

**Best suited for production** is Azure managed hosting in Central India: Indian data residency (PRD §6.2),
automatic point-in-time database backups that meet the 15-minute recovery target (PRD §35), and no servers to patch.
**Best value** is the budget VPS: the same app for a fraction of the cost, but you are responsible for updates and
backups (the scripts automate most of it).

---

## 3. Testing for free

### 3A. From your PC with a Cloudflare tunnel (free, 10 minutes)

Gives your local app a temporary public `https://….trycloudflare.com` address that works on any phone, including
Web Push and "Add to Home screen". It stops when you close the terminal or turn the PC off.

1. Install cloudflared: `winget install --id Cloudflare.cloudflared`
2. Build the web app and let the API serve it (one address for everything):
   ```powershell
   cd C:\Pravin\AppointmentSystem\.claude\worktrees\review-fixes\web
   npm run build
   Remove-Item -Recurse -Force ..\src\SmartQueue.Api\wwwroot -ErrorAction SilentlyContinue
   Copy-Item -Recurse dist ..\src\SmartQueue.Api\wwwroot
   cd ..
   dotnet run --project src/SmartQueue.Api --launch-profile http
   ```
3. In a second terminal: `cloudflared tunnel --url http://localhost:5221`
4. Open the printed `https://…trycloudflare.com` address on your phone.

Notes:
- This runs in **Development** mode: sign-in codes are shown on screen (no SMS) and the Super Admin has no
  authenticator step. Anyone with the link can sign in with any number, so share it only with testers and close it
  afterwards.
- `src/SmartQueue.Api/wwwroot` is ignored by git. To go back to the normal two-server development setup, delete it
  **and run `dotnet clean src/SmartQueue.Api` then `dotnet build src/SmartQueue.Api`** (the build remembers the
  folder, and the API will not start until those files are cleaned).
- For a fixed address (`test.yourdomain.in`), create a named tunnel in the free Cloudflare Zero Trust dashboard.

### 3B. A real server on free credits

Use the [budget VPS steps](#5-step-by-step-budget-vps-scripted) with a provider that gives new accounts credit:

| Provider | New-account offer (check current terms) | Server to choose |
|---|---|---|
| DigitalOcean | Credit for the first 60 days | Basic droplet, 4 GB, Bangalore |
| Microsoft Azure | Credit for the first 30 days | B2s Linux VM, Central India |
| AWS Lightsail | Some plans free for the first 3 months | 4 GB instance, Mumbai |
| Google Cloud | Credit for the first 90 days | e2-medium, Mumbai |

Set a budget alert so you are not charged when the credit ends.

### Free options that do not fit this app

| Option | Problem |
|---|---|
| Oracle Cloud Always Free | The free x86 VMs have 1 GB RAM (too small for SQL Server); the free ARM VMs cannot run SQL Server. |
| Azure SQL Database free offer | The notification worker checks the database every 2 seconds, which keeps it awake and uses the monthly free compute in a few days. |
| Render / Railway / Koyeb free tiers | No SQL Server, and free apps sleep when idle (turn alerts would stop). |
| Azure App Service F1 (free) | 60 CPU minutes a day and no "Always On": the app sleeps and alerts stop. |

---

## 4. Production options and costs

| Option | Monthly cost (approx.) | Region / latency from India | Who maintains it | Backups |
|---|---|---|---|---|
| **Hetzner CX22** (2 vCPU, 4 GB) + Docker Compose | €5–6 | Germany/Finland: ~130–160 ms. Singapore (CPX plans, ~€9–12): ~60 ms | You (scripted) | Nightly script + optional off-site copy |
| **DigitalOcean / AWS Lightsail 4 GB** + Docker Compose | $20–25 | Bangalore / Mumbai: ~10–40 ms | You (scripted) | Nightly script; provider snapshots ~+20% |
| **Azure VM B2s** (2 vCPU, 4 GB) + Docker Compose | ~$35–40 | Central India (Pune) | You (scripted) | Nightly script + Azure Backup (extra) |
| **Azure App Service B1 + Azure SQL Basic/S0** | ~$20–35 (pilot) | Central India | Microsoft (platform) | Automatic, point-in-time, 7–35 days |
| **Azure App Service P0v3 + Azure SQL S1/General Purpose** | ~$80–200 | Central India | Microsoft | Automatic, geo-redundant option |

Other costs (all options):

| Item | Approx. cost |
|---|---|
| Domain name (`.in` / `.com`) | ₹500–1,000 per year |
| HTTPS certificate | Free (Let's Encrypt via Caddy, or the platform's managed certificate) |
| DNS and CDN | Free (Cloudflare) |
| SMS (DLT-registered sender, India) | Roughly ₹0.15–0.30 per SMS, from the SMS provider |
| Off-site backup storage (Backblaze B2 / Cloudflare R2) | A few cents per GB per month |
| Uptime monitoring | Free (UptimeRobot, 5-minute checks on `/health`) |

### Cost-cutting tips

1. **Use SQL Server Express** (free licence, up to 10 GB per database — years of appointments for a pilot). The
   Compose setup already does this.
2. **Run one server** with the app and database together until you outgrow it; upgrade the server size instead of
   adding servers.
3. **Turn SMS off where push works**: SMS is only sent to walk-ins without an account; patients with the app get
   free in-app and Web Push alerts.
4. **Commit for a year** (Azure reserved instances, annual VPS billing) for 20–40% off once the pilot is stable.
5. **Use free services**: Cloudflare (DNS, CDN, DDoS protection), Let's Encrypt, UptimeRobot, GitHub Actions and
   GitHub Container Registry (free for this repository).
6. **Avoid Azure SQL serverless auto-pause expectations**: the 2-second notification poller keeps it running, so a
   small provisioned tier (Basic/S0) is cheaper and predictable.

---

## 5. Step by step: budget VPS (scripted)

Files: [`deploy/vps/`](../deploy/vps) — `docker-compose.yml`, `Caddyfile`, `.env.example`, `scripts/`.

What runs on the server:

| Container | Role |
|---|---|
| `db` | SQL Server 2022 **Express** (free). Not reachable from the internet. |
| `migrate` | Applies database migrations, then exits. Runs before the app starts. |
| `api` | Valmedo (web app + API) on port 8080, internal only. |
| `caddy` | HTTPS on 80/443 with automatic Let's Encrypt certificates. |
| `backup` | Nightly full backup at 02:30 IST, verified, gzipped, kept 14 days. |

### Before you start

- A server: **Ubuntu 24.04, x86-64, 4 GB RAM** (2 GB works for testing), with SSH access.
- A domain name, with an **A record** (for example `app.yourdomain.in`) pointing to the server's IP address.
  If you use Cloudflare DNS, set the record to "DNS only" (grey cloud) until the certificate is issued.
- The container images published (push to GitHub; see [section 1](#1-how-the-app-runs-in-production)).

### Steps

```bash
# 1. On the server: get the deployment files
sudo apt-get update && sudo apt-get install -y git
git clone https://github.com/dhotepravin17/DrAI.git valmedo
cd valmedo/deploy/vps

# 2. Install Docker, firewall, swap, backup folder, automatic security updates
sudo bash scripts/setup.sh

# 3. Create .env and generate secrets (SQL password, JWT key, Web Push keys)
bash scripts/generate-secrets.sh
nano .env      # set DOMAIN, ACME_EMAIL, Push__Subject, Bootstrap__SuperAdmins__0__MobileNumber

# 4. If the images are private: log in once (token with read:packages)
echo <token> | sudo docker login ghcr.io -u dhotepravin17 --password-stdin

# 5. Start everything
sudo docker compose up -d
sudo docker compose ps          # db healthy, migrate exited (0), api/caddy/backup running
curl https://app.yourdomain.in/health
```

6. Open `https://app.yourdomain.in`, sign in with the Super Admin mobile number from `.env`, and set up the
   authenticator app when asked.
7. **Sign-in codes need SMS.** In production, codes are sent only by SMS and are never shown or logged — so
   without the `Sms__*` settings ([section 7](#7-configuration-reference)) nobody can sign in, including the Super
   Admin. Two choices:
   - **Production:** configure the SMS gateway (DLT registration) before step 5.
   - **Test server only:** in `.env`, uncomment `ASPNETCORE_ENVIRONMENT=Development` and
     `Otp__ExposeCodeInResponse=true`, then `sudo docker compose up -d api`. Codes then appear on the sign-in screen,
     exactly like on your PC. Anyone with the address could sign in with any number, so never do this on a server
     real patients use, and remove both lines before going live.

### Useful commands

| Task | Command (in `deploy/vps`) |
|---|---|
| Status | `sudo docker compose ps` |
| App logs | `sudo docker compose logs -f --tail 200 api` |
| Deploy a new version | `sudo bash scripts/update.sh` or `sudo bash scripts/update.sh sha-abc1234` |
| Backup now | `sudo bash scripts/backup-now.sh` |
| Restore | `sudo bash scripts/restore.sh backups/SmartQueue_<stamp>.bak.gz` |
| Restart the app | `sudo docker compose restart api` |
| Stop everything | `sudo docker compose down` (data stays in Docker volumes) |

### Off-site backups (recommended)

Backups in `deploy/vps/backups` are on the same disk as the database. Copy them elsewhere daily, for example with
[rclone](https://rclone.org) to Backblaze B2 or Cloudflare R2:

```bash
sudo apt-get install -y rclone
rclone config                     # create a remote called "offsite"
( crontab -l; echo "0 22 * * * rclone copy /root/valmedo/deploy/vps/backups offsite:valmedo-backups --max-age 48h" ) | crontab -
```

Test a restore every quarter (PRD §35): see [operations/backup-restore.md](operations/backup-restore.md).

---

## 6. Step by step: Azure managed (Central India)

Not scripted as files; these Azure CLI commands set it up. Run them in Azure Cloud Shell (Bash).

```bash
RG=valmedo-prod; LOC=centralindia; APP=valmedo-app; SQL=valmedo-sql-$RANDOM; DB=SmartQueue
SQLPASS='<strong password>'

az group create -n $RG -l $LOC

# Database: Basic (5 DTU, 2 GB) for a pilot; S0/S1 as usage grows. Automatic point-in-time restore is included.
az sql server create -g $RG -n $SQL -l $LOC -u valmedoadmin -p "$SQLPASS"
az sql server firewall-rule create -g $RG -s $SQL -n allow-azure --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0
az sql db create -g $RG -s $SQL -n $DB --service-objective Basic --backup-storage-redundancy Local

# App: Linux App Service B1, running the published container
az appservice plan create -g $RG -n valmedo-plan --is-linux --sku B1
az webapp create -g $RG -p valmedo-plan -n $APP \
  --container-image-name ghcr.io/dhotepravin17/drai:latest
az webapp config set -g $RG -n $APP --always-on true --web-sockets-enabled true --http20-enabled true
az webapp update -g $RG -n $APP --https-only true
az webapp config set -g $RG -n $APP --generic-configurations '{"healthCheckPath": "/health"}'

# Settings (use Key Vault references for secrets in real production)
CONN="Server=tcp:$SQL.database.windows.net,1433;Database=$DB;User ID=valmedoadmin;Password=$SQLPASS;Encrypt=True;"
az webapp config appsettings set -g $RG -n $APP --settings \
  WEBSITES_PORT=8080 \
  ConnectionStrings__Default="$CONN" \
  Bootstrap__ApplyMigrationsOnStartup=true \
  Bootstrap__SuperAdmins__0__MobileNumber=<10-digit mobile> \
  Jwt__SigningKey="$(openssl rand -base64 48)" \
  Proxy__TrustAll=true \
  Auth__AdminMfaRequired=true
```

Then add the Web Push keys (`Push__PublicKey`, `Push__PrivateKey`, `Push__Subject`; generate them with
`deploy/vps/scripts/generate-secrets.sh` on any Linux machine) and the SMS settings, add your domain under
**Custom domains** with a free managed certificate, and enable **Application Insights** for monitoring.

- `Bootstrap__ApplyMigrationsOnStartup=true` is acceptable here because there is exactly one instance. For larger
  setups run the `drai-migrator` image as a one-off job before each release instead.
- Keep **one instance** (do not enable autoscale out) — see [section 1](#1-how-the-app-runs-in-production).
- If the images are private, set **Deployment Center → Registry settings** with a GitHub token (`read:packages`).

---

## 7. Configuration reference

Set these as environment variables (`.env` on the VPS, App settings on Azure). `__` separates sections.

| Setting | Required | Notes |
|---|---|---|
| `ConnectionStrings__Default` | Yes | Set by `docker-compose.yml` on the VPS. |
| `Jwt__SigningKey` | Yes | 32+ random characters. Changing it signs everyone out. |
| `Bootstrap__SuperAdmins__0__MobileNumber` | First run | Creates the first Super Admin. |
| `Bootstrap__ApplyMigrationsOnStartup` | — | `false` with the migrator (VPS); `true` is fine for a single Azure instance. |
| `Push__PublicKey`, `Push__PrivateKey`, `Push__Subject` | For alerts | Web Push (VAPID) keys; `generate-secrets.sh` creates them. Subject is `mailto:` or your site URL. Changing them drops existing push subscriptions. |
| `Sms__Provider=Http`, `Sms__Endpoint`, `Sms__ApiKey`, `Sms__SenderId`, `Sms__DltEntityId`, `Sms__OtpTemplateId`, `Sms__NotificationTemplateId` | For real users | Without SMS, production cannot deliver sign-in codes. Needs TRAI DLT registration. The app refuses to start if `Provider=Http` is set without the required values. |
| `Otp__WebOtpDomain` | Optional | Your domain; enables automatic code filling on Android. |
| `Auth__AdminMfaRequired` | Yes in production | `true`: Super Admins use an authenticator app. |
| `Proxy__TrustAll` / `Proxy__TrustedProxies__0` | Behind a proxy | Lets the app see real client addresses (rate limits, audit logs). `TrustAll=true` only when the app port is not reachable directly (Compose, App Service). |
| `RateLimits__OtpPerMinutePerIp` | Optional | Default 10. Raise (e.g. 60) if many users share one network, such as a hospital Wi-Fi. |
| `Plans__*`, `Retention__*` | Optional | Plan limits and data retention (pending legal decisions OQ-03, OQ-05). |
| `Notifications__WorkerEnabled`, `Retention__WorkerEnabled` | Optional | Leave `true` on the single instance. |

Data protection keys (which protect stored authenticator secrets) are stored in the database table
`DataProtectionKeys`, so they survive restarts and redeployments. Include the database in every backup.

---

## 8. Updates, backups and monitoring

- **Updates (VPS):** every push builds new images; on the server run `sudo bash scripts/update.sh` (backup, pull,
  migrate, restart, health check). Pin a tested version with `sudo bash scripts/update.sh sha-<commit>`.
- **Updates (Azure):** `az webapp config container set … --container-image-name ghcr.io/dhotepravin17/drai:sha-<commit>`
  then restart; or enable continuous deployment from GHCR.
- **Rollback:** deploy the previous `sha-…` tag. Migrations only move forward, so restore the pre-update backup if a
  migration must be undone.
- **Backups:** VPS — nightly at 02:30 IST, 14 days kept, plus off-site copy; Azure SQL — automatic point-in-time.
- **Monitoring:** add `https://<your domain>/health` to UptimeRobot (free). Check
  Admin → Failures for notification problems.
- **Logs:** `sudo docker compose logs api` (VPS) or Log stream / Application Insights (Azure).
- **Load test** before the pilot: [operations/load-testing.md](operations/load-testing.md).

---

## 9. Go-live checklist

- [ ] HTTPS works on your domain; `/health` returns `ok`
- [ ] `Auth__AdminMfaRequired=true`; Super Admin has set up the authenticator app
- [ ] SMS configured with DLT-registered sender and templates; a test sign-in code arrives
- [ ] Web Push keys set; a test alert arrives on Android Chrome and an iPhone home-screen app
- [ ] `Jwt__SigningKey`, SQL password and VAPID private key are stored only on the server / in Key Vault
- [ ] Nightly backups present; off-site copy configured; one restore tested
- [ ] Uptime monitor on `/health`
- [ ] `RateLimits__OtpPerMinutePerIp` suits clinics that share a network
- [ ] Retention periods and plan limits confirmed (PRD OQ-03, OQ-05)
- [ ] Legal review of privacy notice, declaration and consent texts (PRD OQ-02)
- [ ] Load test passed against the production-like server

---

## 10. Android app

- **Now, no extra work:** once deployed on HTTPS, Android users can open the site in Chrome and choose
  **Install app / Add to Home screen**. It opens full-screen with the Valmedo icon and receives Web Push alerts.
- **Play Store listing:** wrap the deployed site as a Trusted Web Activity (Bubblewrap), or build a Capacitor app
  from the same web code with native Firebase push. Both need the production HTTPS address from this guide first.
