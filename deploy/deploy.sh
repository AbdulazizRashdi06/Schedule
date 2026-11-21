#!/usr/bin/env bash
# Deploys the committed code to the Zarqa VPS and (re)starts the container.
# Run from the repository root on the laptop (Git Bash on Windows works):  ./deploy/deploy.sh
# Port 22 is blocked on campus Wi-Fi: use another network or a hotspot.
set -euo pipefail

HOST="${DEPLOY_HOST:-deploy@173.249.40.122}"
KEY="${DEPLOY_KEY:-$HOME/.ssh/zarqa_vps}"
APP="/opt/uni-schedule"
SSH=(ssh -i "$KEY" -o ServerAliveInterval=30 "$HOST")

cd "$(git rev-parse --show-toplevel)"
if [ -n "$(git status --porcelain)" ]; then
  echo "Note: uncommitted changes are NOT deployed (only HEAD is shipped)." >&2
fi
REV="$(git rev-parse --short HEAD)"

echo "==> Uploading $REV to $HOST:$APP"
# Replace the code but keep the server's deploy/.env (the database lives in a Docker volume, not here).
"${SSH[@]}" "set -e; mkdir -p $APP/deploy; [ -f $APP/deploy/.env ] && mv $APP/deploy/.env $APP/.env.keep || true; rm -rf $APP/server $APP/web $APP/deploy $APP/docs $APP/sample-data; mkdir -p $APP/deploy; [ -f $APP/.env.keep ] && mv $APP/.env.keep $APP/deploy/.env || true"
git archive --format=tar HEAD | "${SSH[@]}" "tar -xf - -C $APP && echo $REV > $APP/REVISION"

echo "==> Checking settings"
# Quoted heredoc: everything below runs on the server as written; only APP is passed in.
"${SSH[@]}" "APP=$APP bash -s" <<'REMOTE'
set -euo pipefail
cd "$APP"
DC=(docker compose -f deploy/docker-compose.yml --env-file deploy/.env)
if [ ! -f deploy/.env ]; then
  cp deploy/.env.example deploy/.env
  echo "Created deploy/.env from the example. Edit it on the server (OWNER_IDS, email) and deploy again." >&2
fi
NET=$(grep -E '^ZARQA_NETWORK=' deploy/.env | cut -d= -f2 || true)
NET=${NET:-zarqa_default}
if ! docker network inspect "$NET" >/dev/null 2>&1; then
  echo "Docker network '$NET' not found. Pick the Zarqa one from this list and set ZARQA_NETWORK in $APP/deploy/.env:" >&2
  docker network ls >&2
  exit 1
fi

healthy() {
  for _ in $(seq 1 30); do
    if docker run --rm --network "$NET" curlimages/curl -fsS http://uni-schedule:8080/api/health >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

# Keep the image that is serving now as uni-schedule:previous, so the build doesn't leave it untagged (and prunable).
CUR=$("${DC[@]}" ps -q uni-schedule 2>/dev/null | head -1 || true)
OLD=$( { [ -n "$CUR" ] && docker inspect -f '{{.Image}}' "$CUR"; } 2>/dev/null || docker image inspect -f '{{.Id}}' uni-schedule:latest 2>/dev/null || true)
if [ -n "$OLD" ]; then docker tag "$OLD" uni-schedule:previous; fi

echo "==> Building (the current version keeps serving)"
"${DC[@]}" build

echo "==> Stopping the app and backing up the database"
# Stopped, the app checkpoints SQLite and leaves one quiet schedule.db, so the copy can't be torn.
"${DC[@]}" stop
mkdir -p "$APP/backups"
BACKUP="db-$(date +%Y%m%d-%H%M%S).tgz"
if ! docker run --rm -v uni-schedule_data:/d -v "$APP/backups":/b alpine tar czf "/b/$BACKUP" -C /d .; then
  echo "Backup failed. Starting the current version again." >&2
  rm -f "$APP/backups/$BACKUP"
  "${DC[@]}" start
  exit 1
fi
# Keep the newest 14.
ls -1t "$APP"/backups/db-*.tgz | tail -n +15 | xargs -r rm -f
echo "Backups: $(ls -1 "$APP"/backups/db-*.tgz | wc -l), this one $APP/backups/$BACKUP"

echo "==> Starting the new version"
STARTED=yes
"${DC[@]}" up -d --no-build --remove-orphans || STARTED=no

echo "==> Waiting for /api/health"
if [ "$STARTED" = yes ] && healthy; then
  echo "Healthy."
  # Only now drop untagged images; uni-schedule:previous stays for the next rollback.
  docker image prune -f >/dev/null
  exit 0
fi
echo "Not healthy. Logs:" >&2
"${DC[@]}" logs --tail 50 >&2 || true

if [ -z "$OLD" ]; then
  echo "No previous version to go back to. Fix and deploy again." >&2
  exit 1
fi
echo "==> Rolling back to the previous version and the database from before this deploy" >&2
"${DC[@]}" stop || true
# A failed migration may have changed the database, so put the pre-deploy copy back as well.
if ! docker run --rm -e BACKUP="$BACKUP" -v uni-schedule_data:/d -v "$APP/backups":/b:ro alpine \
  sh -c 'own=$(stat -c %u:%g /d) && rm -rf /d/..?* /d/.[!.]* /d/* && tar xzf "/b/$BACKUP" -C /d && chown -R "$own" /d'; then
  echo "Restoring the database failed, so the app is left stopped. Restore $APP/backups/$BACKUP by hand (deploy/README.md)." >&2
  exit 1
fi
docker tag uni-schedule:previous uni-schedule:latest
"${DC[@]}" up -d --no-build --force-recreate --remove-orphans || true
if healthy; then
  echo "Rolled back: the previous version is serving again. This deploy FAILED." >&2
else
  echo "Rolled back, but the previous version isn't healthy either. Check the logs. Backup: $APP/backups/$BACKUP" >&2
fi
exit 1
REMOTE

echo "==> Deployed $REV. https://schedule.tryzarqa.com"
