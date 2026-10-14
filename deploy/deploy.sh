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
"${SSH[@]}" bash -s <<REMOTE
set -euo pipefail
cd $APP
if [ ! -f deploy/.env ]; then
  cp deploy/.env.example deploy/.env
  echo "Created deploy/.env from the example. Edit it on the server (OWNER_IDS, email) and deploy again." >&2
fi
NET=\$(grep -E '^ZARQA_NETWORK=' deploy/.env | cut -d= -f2)
NET=\${NET:-zarqa_default}
if ! docker network inspect "\$NET" >/dev/null 2>&1; then
  echo "Docker network '\$NET' not found. Pick the Zarqa one from this list and set ZARQA_NETWORK in $APP/deploy/.env:" >&2
  docker network ls >&2
  exit 1
fi

echo "==> Building and starting"
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build --remove-orphans
docker image prune -f >/dev/null

echo "==> Waiting for /api/health"
for i in \$(seq 1 30); do
  if docker run --rm --network "\$NET" curlimages/curl -fsS http://uni-schedule:8080/api/health >/dev/null 2>&1; then
    echo "Healthy."
    exit 0
  fi
  sleep 2
done
echo "Not healthy yet. Logs:" >&2
docker compose -f deploy/docker-compose.yml --env-file deploy/.env logs --tail 50 >&2
exit 1
REMOTE

echo "==> Deployed $REV. https://schedule.tryzarqa.com"
