# Deploying to the Zarqa VPS

The app runs as one container (`uni-schedule`, port 8080) next to the Zarqa stack. It joins Zarqa's Docker network so Zarqa's Caddy can proxy to it. Data (SQLite database + sign-in keys) lives in the `uni-schedule_data` Docker volume.

Run everything below **from the laptop**: the SSH key (`~/.ssh/zarqa_vps`) is only there, and campus Wi-Fi blocks port 22.

## First deploy

1. **Find Zarqa's network name** (probably `zarqa_default`):
   ```bash
   ssh -i ~/.ssh/zarqa_vps deploy@173.249.40.122 docker network ls
   ```
2. **Ship the code** (from the repo root, in Git Bash):
   ```bash
   ./deploy/deploy.sh
   ```
   The first run creates `/opt/uni-schedule/deploy/.env` from `.env.example`. If the network name differs, the script stops and lists the networks.
3. **Edit the settings on the server**, then run `./deploy/deploy.sh` again:
   ```bash
   ssh -i ~/.ssh/zarqa_vps deploy@173.249.40.122 nano /opt/uni-schedule/deploy/.env
   ```
   | Variable | Meaning |
   |---|---|
   | `ZARQA_NETWORK` | Zarqa's Docker network from step 1 |
   | `OWNER_IDS` | Comma-separated student IDs that are owners, e.g. `24-0161,23-0042` |
   | `AUTH_TEST_MODE` | `true` shows the sign-in code in the app (never for owners) until email works |
   | `EMAIL_PROVIDER` | `log` (codes in the logs) or `resend` |
   | `RESEND_API_KEY`, `EMAIL_FROM` | For `resend`; the sender's domain must be verified in Resend |

   Optional, each left out keeps the server's default (listed in `.env.example`):

   | Variable | Default | Meaning |
   |---|---|---|
   | `RATE_LIMIT_REQUEST_CODE`, `RATE_LIMIT_VERIFY`, `RATE_LIMIT_PUBLIC_TIMETABLE` | 1000, 2000, 600 | Per IP per 5 minutes: sign-in code requests, code checks, public timetable reads |
   | `SYNC_EXTRA_CODES` | `CS1102-L` | Extra EduWave course codes for Classy Sync, comma-separated; empty for none |
   | `PLANNER_ENABLED`, `PLANNER_AI_URL`, `PLANNER_AI_MODEL` | `false`, `http://codex:8090/v1/chat/completions`, `gpt-6-luna` | Semester planner; the owners' dashboard setting wins over `PLANNER_ENABLED` |
   | `ANALYTICS_ENABLED` | `true` | Usage analytics |
   | `PUSH_SUBJECT` | `mailto:schedule@tryzarqa.com` | Contact sent with push notifications |

   Only the variables in `deploy/docker-compose.yml` reach the app, and they win over `appsettings.json`. To add another setting, map it there and commit (edits to the server's copy are replaced on every deploy).
4. **Add the site to Zarqa's Caddy.** Append `deploy/Caddyfile.snippet` to `C:\Projects\LostraAi\deploy\Caddyfile`, deploy Zarqa as usual (or reload Caddy on the server), e.g.:
   ```bash
   ssh -i ~/.ssh/zarqa_vps deploy@173.249.40.122 "cd /opt/<zarqa> && docker compose exec caddy caddy reload --config /etc/caddy/Caddyfile"
   ```
5. Open <https://schedule.173-249-40-122.sslip.io>.

Owners sign in with their student ID (owners' codes are never shown in test mode, so with `EMAIL_PROVIDER=log` read the code from the logs, see below).

## Dashboard accounts (RSA)

Create the first username account on the server; it prints a temporary password that must be changed at first sign-in on `/admin/login`:

```bash
ssh -i ~/.ssh/zarqa_vps deploy@173.249.40.122 \
  "cd /opt/uni-schedule && docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec uni-schedule dotnet UniSchedule.Api.dll create-admin rsa-office rsa"
```

Use `owner` instead of `rsa` for an owner account. Running it again for an existing username resets the password. After that, owners and RSA can add accounts from the dashboard.

## Everyday

- **Update:** commit, then `./deploy/deploy.sh` (only committed code is shipped). It builds the new image while the site keeps running, stops the app, backs up the database to `/opt/uni-schedule/backups` (the newest 14 are kept), and starts the new version. If `/api/health` doesn't answer within a minute, it puts back the previous image (kept as `uni-schedule:previous`) and that backup, starts it, and fails.
- **Logs / sign-in codes:**
  `ssh -i ~/.ssh/zarqa_vps deploy@173.249.40.122 "cd /opt/uni-schedule && docker compose -f deploy/docker-compose.yml --env-file deploy/.env logs -f --tail 100"`
- **Backup the database:** every deploy makes one. By hand, stop the app first so SQLite isn't copied mid-write (the site is down for a few seconds):
  `ssh -i ~/.ssh/zarqa_vps deploy@173.249.40.122 "cd /opt/uni-schedule && docker compose -f deploy/docker-compose.yml --env-file deploy/.env stop && docker run --rm -v uni-schedule_data:/d -v /opt/uni-schedule:/b alpine tar czf /b/backup-\$(date +%F).tgz -C /d .; docker compose -f deploy/docker-compose.yml --env-file deploy/.env start"`
- **Restore a backup:** stop the app, empty the volume and unpack the whole archive, so `schedule.db` comes back together with any `schedule.db-wal` and the `keys/` from the same backup, then start it:
  `ssh -i ~/.ssh/zarqa_vps deploy@173.249.40.122 "cd /opt/uni-schedule && docker compose -f deploy/docker-compose.yml --env-file deploy/.env stop && docker run --rm -v uni-schedule_data:/d -v /opt/uni-schedule/backups:/b:ro alpine sh -c 'own=\$(stat -c %u:%g /d) && rm -rf /d/* && tar xzf /b/db-YYYYMMDD-HHMMSS.tgz -C /d && chown -R \$own /d' && docker compose -f deploy/docker-compose.yml --env-file deploy/.env start"`
- Database migrations run automatically at startup.

## Deploying from GitHub

`.github/workflows/deploy.yml` runs the tests and the web build, then runs the same `./deploy/deploy.sh` on a GitHub runner. It deploys on every push to `main`, or by hand from Actions → Deploy → Run workflow. This works when the laptop is away or on campus Wi-Fi.

It needs repository secrets (Settings → Secrets and variables → Actions):

- `DEPLOY_SSH_KEY` (required): the whole private key, `~/.ssh/zarqa_vps` including the BEGIN/END lines. If the laptop key is lost, make a new ed25519 key, add its public half to `/home/deploy/.ssh/authorized_keys` from the hosting provider's web console, and use the new private key here.
- `DEPLOY_KNOWN_HOSTS` (optional): the server's `known_hosts` line (`ssh-keygen -F 173.249.40.122`) to pin the host key. Without it the job trusts whatever key the server presents.

The server's `deploy/.env` is kept as is, like a laptop deploy. `./deploy/deploy.sh` still works from the laptop.
