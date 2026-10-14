# Handoff: deploy from GitHub Actions (for the next local session)

Paste this into a Claude Code session on the laptop, in the repo https://github.com/AbdulazizRashdi06/Schedule.

## Where things stand

- Design v3 is done on branch `design-v3` (see `docs/handoff-design-v3.md`). Not merged, not deployed.
- Deploying today needs the laptop: `./deploy/deploy.sh` uses SSH with `~/.ssh/zarqa_vps` (port 22, blocked on campus Wi-Fi).
- Goal: deploy from GitHub Actions instead, so a broken laptop or campus Wi-Fi doesn't block a release.
- A cloud session tried to add this but the permission check stopped it (it counts as a production deploy), so nothing was committed. Do it locally where the owner can approve it.

## Steps

1. **Add the key as a secret (owner does this, never paste it in chat).**
   GitHub → Settings → Secrets and variables → Actions → New repository secret:
   - `DEPLOY_SSH_KEY`: the whole contents of `~/.ssh/zarqa_vps` (BEGIN/END lines included).
   - Optional `DEPLOY_KNOWN_HOSTS`: the server's line from `~/.ssh/known_hosts` (`ssh-keygen -F 173.249.40.122`), to pin the host key.
   From the laptop, Claude can do this without the key ever showing in chat (needs the `gh` CLI, signed in):
   `gh secret set DEPLOY_SSH_KEY --repo AbdulazizRashdi06/Schedule < ~/.ssh/zarqa_vps`
   and `ssh-keygen -F 173.249.40.122 -f ~/.ssh/known_hosts | grep -v '^#' | gh secret set DEPLOY_KNOWN_HOSTS --repo AbdulazizRashdi06/Schedule`.
   If the laptop key is lost: make a new ed25519 key, add its public half to `/home/deploy/.ssh/authorized_keys` through the hosting provider's web console, and use the new private key as the secret.

2. **Add `.github/workflows/deploy.yml`** on `design-v3` (or main). Decide with the owner: deploy on every push to `main`, or manual only (drop the `push:` trigger and keep `workflow_dispatch`).

   ```yaml
   # Deploys to the Zarqa VPS from GitHub. Secrets: DEPLOY_SSH_KEY (required), DEPLOY_KNOWN_HOSTS (optional).
   name: Deploy

   on:
     push:
       branches: [main]
     workflow_dispatch:

   concurrency:
     group: deploy
     cancel-in-progress: false

   jobs:
     test:
       runs-on: ubuntu-latest
       steps:
         - uses: actions/checkout@v4
         - uses: actions/setup-dotnet@v4
           with:
             dotnet-version: 10.0.x
         - run: dotnet test
           working-directory: server
         - uses: actions/setup-node@v4
           with:
             node-version: 22
             cache: npm
             cache-dependency-path: web/package-lock.json
         - run: npm ci && npx eslint . && npm run build
           working-directory: web

     deploy:
       needs: test
       runs-on: ubuntu-latest
       steps:
         - uses: actions/checkout@v4
         - name: Set up the SSH key
           env:
             DEPLOY_SSH_KEY: ${{ secrets.DEPLOY_SSH_KEY }}
             DEPLOY_KNOWN_HOSTS: ${{ secrets.DEPLOY_KNOWN_HOSTS }}
           run: |
             if [ -z "$DEPLOY_SSH_KEY" ]; then echo "Add the DEPLOY_SSH_KEY secret first." >&2; exit 1; fi
             mkdir -p ~/.ssh && chmod 700 ~/.ssh
             printf '%s\n' "$DEPLOY_SSH_KEY" > ~/.ssh/zarqa_vps && chmod 600 ~/.ssh/zarqa_vps
             if [ -n "$DEPLOY_KNOWN_HOSTS" ]; then printf '%s\n' "$DEPLOY_KNOWN_HOSTS" > ~/.ssh/known_hosts
             else ssh-keyscan -H 173.249.40.122 > ~/.ssh/known_hosts 2>/dev/null; fi
         - run: ./deploy/deploy.sh
         - name: Remove the key
           if: always()
           run: rm -f ~/.ssh/zarqa_vps
   ```

   `deploy/deploy.sh` already reads the key from `~/.ssh/zarqa_vps` and the host `deploy@173.249.40.122`, so it needs no changes. Check `web/package-lock.json` exists (the cache step uses it).

3. **Add a short section to `deploy/README.md`**: "Deploying from GitHub": the workflow, the secrets, and that the server's `deploy/.env` is untouched.

4. **Ship v3**: open a PR `design-v3` → `main`, let the `test` job pass, merge. With the push trigger the deploy runs on merge; otherwise Actions → Deploy → Run workflow.

5. **Check** https://schedule.173-249-40-122.sslip.io: student sign-in, Today, `/admin` Home (Needs attention, Recent uploads). The `UploadLog` migration runs at startup. If the job fails, its log shows the container logs (deploy.sh prints them when the health check fails).

## Notes

- GitHub's runners can reach port 22; the VPS firewall must allow SSH from anywhere (it does today, the laptop connects from varying IPs).
- Keep `./deploy/deploy.sh` working from the laptop too; the workflow just runs the same script.
