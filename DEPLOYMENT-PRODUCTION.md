# Production Deployment Runbook — Temporary Cash Payment Overdue HR Notification

Hand this document to the deploy team along with the bundle. It lists every
parameter that must be reviewed/changed before deploy and walks through the
8-step procedure end-to-end.

For the comprehensive engineering reference (architecture, rollback, risks),
see `DEPLOYMENT-TemporaryCashPayment-Overdue.md`.

---

## Part A — Parameters to review / change before deploy

### A.1 `appsettings.json` on the Production Portal

Section `CaseActivities:TemporaryCashPayment` — review every value before applying:

| Key | Dev value | Set in prod to |
|---|---|---|
| `OverdueDelayDays` | `7` | Confirm policy (likely 7). Lower for first-week pilot if you want to see drains fire sooner. |
| `OverdueTaskActivityName` | `"Submit Original Invoices"` | Exact Activity Name (or Title) of the user task in the published Temporary Cash Payment workflow. Drain joins on `ActivityDefinition.Name = @name OR ad.Title = @name` — case-sensitive. |
| `EmailTemplateName` | `"OnTemporaryCashInvoiceOverdue"` | Keep, unless prod uses a different template name. Must match the row inserted by `insert_template.sql`. |
| `FromDisplayName` | `"Case Notifications"` | Display name in the From header. |
| `HrRecipients` | `"hr@intalio.com"` | **MUST CHANGE.** Real HR distribution list for prod. Semicolon or comma separated. |
| `HrCcRecipients` | `""` | CC list. May stay empty. |
| `HrBccRecipients` | `""` | BCC list. May stay empty. |

### A.2 `DB Scripts/register_scheduler_drainer.sql`

| Variable | Dev value | Set in prod to |
|---|---|---|
| `@AssemblyName` | `N'Shared.Activities'` | Keep — locked by the csproj's `AssemblyName`. |
| `@JobId` | `N'case-tcp-overdue-drainer'` | Keep, unless prod has a naming convention for recurring jobs. |
| `@Cron` | `N'*/5 * * * *'` | Confirm. Every 5 min is typical. Use `*/1 * * * *` for first-day verification (see drains fire quickly), then reset. |
| `@TimeZoneId` | `N'Arabian Standard Time'` | Confirm matches the Portal's time zone (auto-matches what `CrawlerServiceIndexing` uses). |
| `@Queue` | `N'default'` | Confirm prod Hangfire has a `default` queue worker. Look at the existing `CrawlerServiceIndexing` row in `Scheduler.Hash` to see which queues prod uses; if there's no `default`, change this to whatever Hangfire's main worker pool drains. |

### A.3 `deploy-Shared.Activities.ps1` parameters

Defaults are baked in; override at run time only if prod differs.

| Parameter | Default | Override when… |
|---|---|---|
| `-PortalPath` | `C:\Program Files\Intalio\UC_CasePortal` | Portal is installed elsewhere (custom drive, multi-tenant install). |
| `-DllPath` | `<script folder>\Shared.Activities.dll` | The DLL is not next to the script in the bundle. |
| `-AppPoolName` | `UC_CasePortal` | The prod IIS app pool is named differently. |
| `-SkipPatchVersion` | (auto-detect from deps.json) | Only if Intalio renamed `Intalio.Case.Portal.Core` in a future Portal release and auto-detect can't find it. |

### A.4 SQL command placeholders

Across every SQL command in this runbook, substitute:

| Placeholder | Set to |
|---|---|
| `<prod-sql>` | Production SQL Server name/instance (e.g. `sql-prod-01\PROD`). |
| `<CaseDB>` | Production Case DB name (commonly `UC_Case`, but verify with the DBA). |

---

## Part B — Deployment procedure (8 steps)

### Step 1 — Build (dev machine, one-time per release)

```powershell
cd <repo-root>\ActivityTester
dotnet build Shared.Activities\Shared.Activities.csproj -c Release
```

Output: `Shared.Activities\bin\Release\net8.0\Shared.Activities.dll` (~155 KB,
assembly name locked to `Shared.Activities`).

### Step 2 — Prepare the bundle

Create a folder containing **exactly these 4 files**:

```
deploy-bundle\
  deploy-Shared.Activities.ps1                  (from Shared.Activities\)
  Shared.Activities.dll                          (from Shared.Activities\bin\Release\net8.0\)
  insert_template.sql                            (from DB Scripts\)
  register_scheduler_drainer.sql                 (from DB Scripts\)
```

Hand this folder to the deploy team. They don't need access to source.

### Step 3 — Apply database changes (deploy team, against prod DB)

Run from any machine with `sqlcmd` and network access to the prod SQL instance.

```powershell
# 3a) Email template — idempotent upsert of OnTemporaryCashInvoiceOverdue
sqlcmd -S <prod-sql> -E -d <CaseDB> -I -i .\insert_template.sql

# Expected output line:
#   Inserted new NotificationTemplate row: OnTemporaryCashInvoiceOverdue
# or (on re-run):
#   Updated existing NotificationTemplate row: OnTemporaryCashInvoiceOverdue

# 3b) Hangfire recurring-job registration
#     BEFORE running: open register_scheduler_drainer.sql and verify
#     @Cron, @TimeZoneId, @Queue match prod (see Part A.2 above).
sqlcmd -S <prod-sql> -E -d <CaseDB> -I -i .\register_scheduler_drainer.sql

# Expected output at the bottom:
#   6 Hash rows  (CreatedAt, Cron, Job, Queue, TimeZoneId, V)
#   1 Set row    (recurring-jobs / case-tcp-overdue-drainer)
```

Both scripts are **idempotent** — safe to re-run on a partially-deployed
environment.

### Step 4 — Update the Portal's `appsettings.json` (deploy team, on Portal machine)

Locate `<PortalPath>\appsettings.json` (default `C:\Program Files\Intalio\UC_CasePortal\appsettings.json`).

Inside the existing `CaseActivities` block, add the `TemporaryCashPayment`
sub-block with the values from **Part A.1**:

```json
"CaseActivities": {
  /* ...existing keys (LogDirectory, StorageBaseUrl, IAM, CaseAuth, DmsAuth, Email, ...)... */

  "TemporaryCashPayment": {
    "OverdueDelayDays": 7,
    "OverdueTaskActivityName": "Submit Original Invoices",
    "EmailTemplateName": "OnTemporaryCashInvoiceOverdue",
    "FromDisplayName": "Case Notifications",
    "HrRecipients": "hr-distribution@unioncoop.ae",   /* <-- prod value */
    "HrCcRecipients": "",
    "HrBccRecipients": ""
  }
}
```

Save the file. **Don't restart the Portal yet** — the deploy script in
Step 5 will do that.

Validate JSON shape before continuing (any linter, or just inspect the file —
make sure the new block has a trailing comma if it isn't the last entry in
`CaseActivities`).

### Step 5 — Run the deploy script (deploy team, elevated PowerShell on Portal machine)

```powershell
# From the deploy-bundle folder, in an ELEVATED PowerShell session:
.\deploy-Shared.Activities.ps1

# OR with overrides if prod install differs from defaults:
.\deploy-Shared.Activities.ps1 `
    -PortalPath  "D:\Apps\IntalioCasePortal" `
    -AppPoolName "Intalio.Case.Prod"
```

The script prints 8 sections. Every line should be:
- `+` green = success / step completed
- `!` yellow = idempotent skip (entry already present from a previous run)
- `-` red = failure; script stops and the cause is printed

If you see `-`, **stop and investigate** before proceeding. The script
auto-rolls back any deps.json patch that produced invalid JSON, and the
backup is at `Intalio.Case.Portal.deps.json.bak-<timestamp>` next to the
original.

What the script does in this step:
1. Pre-flight checks (paths, DLL, app pool existence).
2. Auto-detects the `Intalio.Case.Portal.Core` version from existing deps.json.
3. Stops the app pool.
4. Backs up `Intalio.Case.Portal.deps.json`.
5. Patches deps.json (`Shared.Activities/1.0.0.0` in both `targets` and `libraries`).
6. Validates the patched JSON; auto-rolls back if invalid.
7. Copies `Shared.Activities.dll` into the Portal bin folder.
8. Starts the app pool back up, waits for `Started`.

### Step 6 — Wire the activity onto the workflow (Case Designer)

Open the **Temporary Cash Payment** workflow in Case Designer:

1. Add a **Code Activity** step **immediately before** the `Submit Original Invoices` user task.
2. Class Name: `Shared.Activities.ScheduleHrNotificationActivity`
   Assembly:   `Shared.Activities`
   (Use the "existing class" / class browser picker. **Do NOT paste the .cs source** — paste-source mode produces a random in-memory assembly name on every recompile and Hangfire cannot resolve it.)
3. Save and publish the workflow.

### Step 7 — Harden the app pool (deploy team, elevated PowerShell, one-time)

Hangfire's worker only runs while w3wp is alive. Without this step, the
drain pauses every time IIS retires the idle app pool (default 20 min idle).

```powershell
Import-Module WebAdministration
Set-ItemProperty IIS:\AppPools\UC_CasePortal -Name processModel.idleTimeout       -Value "00:00:00"
Set-ItemProperty IIS:\AppPools\UC_CasePortal -Name recycling.periodicRestart.time -Value "00:00:00"
Restart-WebAppPool UC_CasePortal
```

These settings persist across upgrades — apply once per environment.

### Step 8 — Verification (deploy team)

Run in this order. Wait ~90 s after Step 5 before starting (one cron tick
must pass for Hangfire to fire the recurring job for the first time).

```powershell
# 8a) Hangfire worker alive
sqlcmd -S <prod-sql> -E -d <CaseDB> -I -Q "SELECT Id, LastHeartbeat FROM Scheduler.Server;"
# Expected: at least 1 row, LastHeartbeat within the last minute.

# 8b) Recurring job fired successfully
sqlcmd -S <prod-sql> -E -d <CaseDB> -I -Q "SELECT [Field], CAST([Value] AS NVARCHAR(MAX)) FROM Scheduler.Hash WHERE [Key] = 'recurring-job:case-tcp-overdue-drainer' ORDER BY [Field];"
# Expected: LastJobId + LastExecution + NextExecution populated; NO Error field.
# Job value should read:
#   {"t":"Shared.Activities.ScheduleHrNotificationActivity, Shared.Activities","m":"Drain"}

# 8c) Trigger a real Temporary Cash Payment workflow once, then confirm insert
sqlcmd -S <prod-sql> -E -d <CaseDB> -I -Q "SELECT * FROM dbo.CasePendingNotifications ORDER BY Id DESC;"
# Expected: new row with Status='pending', FireAt = now + OverdueDelayDays.

# 8d) OPTIONAL — force-fire a drain to see the full pipeline same-day
sqlcmd -S <prod-sql> -E -d <CaseDB> -I -Q "INSERT INTO dbo.CasePendingNotifications (DocumentId, ActivityName, FireAt, Status) VALUES (<open-task DocId>, 'Submit Original Invoices', DATEADD(MINUTE,-1,SYSUTCDATETIME()), 'pending');"

# Wait one cron tick (<= 5 min), then:
sqlcmd -S <prod-sql> -E -d <CaseDB> -I -Q "SELECT TOP 5 Id, Status, Attempts, LastError FROM dbo.CasePendingNotifications ORDER BY Id DESC;"
# Expected: that row's Status flipped to:
#   'sent'    - HR got the email (task was still open)
#   'skipped' - task was closed in time (user submitted before deadline)
#   'failed'  - exception occurred; LastError column has details
```

Tail `C:\Logs\Case\ScheduleHrNotificationActivity-<today>.log` for:

```
[..] DRAIN  claimed N due row(s).
[..] DRAIN  processing row #<id>  DocumentId=...  activity='Submit Original Invoices'
[..] Reminder email sent for DocumentId=...
```

Those three lines confirm end-to-end: cron tick -> claim -> task-status
check -> email -> mark sent.

---

## Part C — Post-deploy SOP for every Portal upgrade

Add this line to the Case Portal upgrade procedure document at your
organisation:

> **After the Intalio Case Portal installer finishes (it overwrites the
> bin folder + deps.json), re-run `deploy-Shared.Activities.ps1` from
> the deploy-bundle folder in elevated PowerShell. The script is
> idempotent and safe to re-run on an already-patched Portal.**

The other 7 steps in Part B survive Portal upgrades and don't need
re-applying:

| Step | Survives upgrade? | Notes |
|---|---|---|
| 3 — DB changes | Yes | Tables/templates stay; Scheduler.Hash row stays. |
| 4 — appsettings.json | **Usually yes**, but verify | Most Portal installers preserve customer config. Double-check the `CaseActivities:TemporaryCashPayment` block is still there after each upgrade. |
| 6 — Designer workflow wiring | Yes | Stored in the workflow definition table. |
| 7 — App-pool hardening | Yes | IIS metadata persists. |

If the Portal installer DOES overwrite appsettings.json (rare, but possible),
re-apply Step 4 too.

---

## Rollback procedure

| Step done | How to undo |
|---|---|
| DLL deployed (Step 5) | Delete `<PortalPath>\Shared.Activities.dll`, restart app pool. |
| deps.json patched (Step 5) | Restore from `Intalio.Case.Portal.deps.json.bak-<timestamp>`, restart app pool. |
| Recurring job registered (Step 3b) | `DELETE FROM Scheduler.Hash WHERE [Key]='recurring-job:case-tcp-overdue-drainer'; DELETE FROM Scheduler.[Set] WHERE [Key]='recurring-jobs' AND [Value]='case-tcp-overdue-drainer';` |
| Template inserted (Step 3a) | `DELETE FROM NotificationTemplate WHERE Name='OnTemporaryCashInvoiceOverdue';` |
| Pending queue table | `DROP TABLE dbo.CasePendingNotifications;` (only after confirming no in-flight rows you care about). |
| Designer workflow change (Step 6) | Re-publish the previous workflow version. |
| App-pool timeouts (Step 7) | Re-apply your org's defaults (typically `idleTimeout="00:20:00"`, `periodicRestart.time="1.05:00:00"`). |
