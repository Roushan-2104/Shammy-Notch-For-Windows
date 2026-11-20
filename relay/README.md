# Shammy report relay

When Shammy crashes, or an error keeps repeating, it asks the user whether to send a report and shows exactly what
would be sent. A report the user sends goes to this small Cloudflare Worker, which:

- **for a new problem** opens a full issue in the **private** repo `shammy-reports` (GitHub emails it to you) and a
  short summary issue in the **public** repo, with no log lines or notes;
- **for the same problem again** stores it, and at **9:00 PM India time** posts one digest comment per problem on the
  private issue (GitHub emails that too) and updates the counter on the public issue;
- **for a problem you closed as fixed** that comes back in a newer version, reopens both issues.

It runs on Cloudflare's free plan. A day of Shammy reports is a tiny fraction of the free limits (100,000 requests,
100,000 database writes, 5 scheduled jobs). No domain and no email service are needed.

Anyone can reach the relay's address, so it caps what it files: 20 reports a day from one connection and 15 new
problems a day in total. Connections are kept only as a hash that changes every day.

## Set it up (once, about 15 minutes)

You need: a GitHub account (you have one), a free Cloudflare account, and Node.js (installed on this PC).

### 1. The private repo

On GitHub, create a new repository named **`shammy-reports`**, set to **Private**. Leave it empty.

### 2. A token that can only file issues

GitHub › Settings › Developer settings › Personal access tokens › **Fine-grained tokens** › Generate new token.

- Name: `shammy-relay`
- Expiration: 1 year (put a reminder in your calendar to renew it)
- Repository access: **Only select repositories** › `Shammy-Notch-For-Windows` and `shammy-reports`
- Permissions › Repository permissions › **Issues: Read and write** (Metadata: Read is added by itself)

Copy the token. Don't paste it anywhere except step 4, and don't share it with anyone, Claude included.

### 3. Cloudflare

Sign up at https://dash.cloudflare.com/sign-up (free). In **Workers & Pages**, pick your `workers.dev` subdomain when
it asks (for example `roushan`; the relay will live at `shammy-relay.<subdomain>.workers.dev`).

### 4. Put the relay online

In a terminal, in this folder (`Halo\relay`):

```powershell
npx wrangler login
```

```powershell
npx wrangler d1 create shammy-relay
```

Copy the `database_id` it prints into `wrangler.toml`, in place of `PASTE-DATABASE-ID-HERE`. Then:

```powershell
npx wrangler d1 execute shammy-relay --remote --file=schema.sql
```

```powershell
npx wrangler secret put GITHUB_TOKEN
```

Paste the token from step 2 when it asks (it isn't shown or saved in any file).

```powershell
npx wrangler deploy
```

It prints the relay's address, `https://shammy-relay.<subdomain>.workers.dev`. Open it in a browser: it should say
`Shammy relay`.

### 5. Tell Shammy where it is

Send the address to Claude (it isn't a secret). It goes into `Relay` in `src/Shammy.App/Infrastructure/Reports/ReportRelay.cs`.

### 6. Make sure GitHub emails you

GitHub › Settings › Notifications: under **Subscriptions › Watching**, tick **Email**. You watch your own new repos
automatically, so every new issue and digest comment in `shammy-reports` arrives as an email.

## Changing it later

- Code: edit `src/`, run `npm test`, then `npx wrangler deploy`.
- A new token (yearly): `npx wrangler secret put GITHUB_TOKEN` again.
- Logs: `npx wrangler tail` shows what the relay is doing, live.
