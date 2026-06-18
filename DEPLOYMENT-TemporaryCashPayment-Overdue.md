# Production Deployment Checklist — Temporary Cash Payment Overdue HR Notification

End-to-end deployment guide for the *"Submit Original Invoices"* 7-day deadline
reminder. Apply steps in order — each one depends on the previous.

Feature flow:

1. Workflow runs `ScheduleHrNotificationActivity.Execute` on Temporary Cash Payment cases →
   inserts a row in `dbo.CasePendingNotifications` with `FireAt = now + 7 days`.
2. Intalio's Scheduler (Hangfire under the `Scheduler` schema) fires
   `Shared.Activities.ScheduleHrNotificationActivity.Drain()` every 5 minutes via
   a recurring job entry in `Scheduler.Hash` / `Scheduler.[Set]`.
3. `Drain()` claims due rows, re-checks each row's `Submit Original Invoices`
   task status, and emails HR (`OnTemporaryCashInvoiceOverdue` template) only
   for tasks still open. Row flips `pending → processing → sent | skipped | failed`.

---

## 1. Source code (commit to git)

| Path | Purpose |
|---|---|
| `Case Code Activity Classes/Investment/Temporary Cash Payment/ScheduleHrNotificationActivity.cs` | New activity: insert pending row + opportunistic drain + parameterless `Drain` static for the Scheduler to invoke. |
| `Shared.Activities/Shared.Activities.csproj` | New class-library project — builds the activity tree into a fixed-name `Shared.Activities.dll`. |
| `Shared.Activities/deploy-Shared.Activities.ps1` | **Idempotent PowerShell deploy script** — handles bin copy + deps.json patch + app-pool recycle. Re-run after every Portal upgrade. |
| `Shared.Activities/README.md` | Build + deploy reference. |
| `ActivityTester.csproj` | Add `<Compile Remove="Shared.Activities\**" />` + matching `None` / `EmbeddedResource` removes (prevents duplicate-AssemblyAttribute error on solution rebuild). |
| `DB Scripts/insert_template.sql` | Appended idempotent upsert for the `OnTemporaryCashInvoiceOverdue` template. |
| `DB Scripts/register_scheduler_drainer.sql` | New: registers the recurring drain job in `Scheduler.Hash` + `Scheduler.[Set]`. |
| `lib/appsettings.json` | Reference example of the `CaseActivities:TemporaryCashPayment` config block (the real change happens on the prod machine's appsettings — see step 2). |

## 2. Production `appsettings.json` — append under `CaseActivities`

```json
"TemporaryCashPayment": {
  "OverdueDelayDays": 7,
  "OverdueTaskActivityName": "Submit Original Invoices",
  "EmailTemplateName": "OnTemporaryCashInvoiceOverdue",
  "FromDisplayName": "Case Notifications",
  "HrRecipients": "<prod HR email list>",
  "HrCcRecipients": "",
  "HrBccRecipients": ""
}
```

Replace `<prod HR email list>` with the real semicolon/comma-separated recipients.

## 3. Build the DLL

```powershell
dotnet build Shared.Activities\Shared.Activities.csproj -c Release
# Output:
#   Shared.Activities\bin\Release\net8.0\Shared.Activities.dll
#   Assembly name locked at: "Shared.Activities"
```

The DLL is small (~155 KB). It references Portal-side DLLs (`Intalio.Case.Core`,
`Intalio.Case.Portal.Core`, `Intalio.Core`, `Newtonsoft.Json`,
`Microsoft.Data.SqlClient`, `Aspose.Words`, `Aspose.PDF`, `SkiaSharp`) with
`<Private>false</Private>` so it doesn't bundle them — the Portal already
ships them in its bin folder.

## 4. Deploy to the Portal bin folder + patch deps.json

**Use the included script — `Shared.Activities/deploy-Shared.Activities.ps1`.**
It's idempotent (safe to re-run) and consolidates the four manual steps
(stop app pool → backup deps.json → patch deps.json → copy DLL → start
app pool) into one command. Designed to be the deploy team's single
post-upgrade command.

Bundle to ship to the deploy team:

```
deploy-Shared.Activities.ps1      ← from Shared.Activities/
Shared.Activities.dll             ← from Shared.Activities/bin/Release/net8.0/
```

On the target Portal machine, drop both files into the same folder and
run **elevated PowerShell**:

```powershell
.\deploy-Shared.Activities.ps1
```

Override parameters if the prod install differs from the defaults:

```powershell
.\deploy-Shared.Activities.ps1 `
    -PortalPath  "D:\Apps\CasePortal" `
    -DllPath     ".\Shared.Activities.dll" `
    -AppPoolName "Intalio.Case"
```

The script auto-detects the Intalio.Case.Portal.Core version from the
existing deps.json, so it keeps working when Intalio bumps the Portal
version (e.g. 9.3 → 9.4). If a future Portal release restructures
deps.json sections, the script auto-rolls back the deps.json edit and
exits with an error — original file is preserved via timestamped `.bak`.

### What it does internally (for reference)

1. Stop app pool.
2. Back up `Intalio.Case.Portal.deps.json` → `Intalio.Case.Portal.deps.json.bak-<timestamp>`.
3. Insert `"Shared.Activities/1.0.0.0"` into the `targets[".NETCoreApp,Version=v8.0"]` block:

```json
"Shared.Activities/1.0.0.0": {
  "runtime": {
    "Shared.Activities.dll": {
      "assemblyVersion": "1.0.0.0",
      "fileVersion": "1.0.0.0"
    }
  }
},
```

4. Insert `"Shared.Activities/1.0.0.0"` into the `libraries` block:

```json
"Shared.Activities/1.0.0.0": {
  "type": "project",
  "serviceable": false,
  "sha512": ""
},
```

5. Validate JSON; auto-rollback if broken.
6. Copy `Shared.Activities.dll` into the Portal bin folder.
7. Start app pool, wait for `Started` state.

## 5. (Skipped — handled by step 4)

## 6. Database

Run against the production Case DB:

```powershell
# Adds OnTemporaryCashInvoiceOverdue NotificationTemplate row
sqlcmd -S <prod-sql> -E -d UC_Case -I `
       -i "DB Scripts\insert_template.sql"

# Adds Scheduler.Hash + Scheduler.[Set] entries for the recurring drain job
sqlcmd -S <prod-sql> -E -d UC_Case -I `
       -i "DB Scripts\register_scheduler_drainer.sql"
```

Both scripts are **idempotent** (safe to re-run).

`dbo.CasePendingNotifications` is auto-created by the activity on its first
`Execute` — no manual DDL step needed. If your prod DB locks down CREATE TABLE
permissions, run the DDL block out of the activity's `EnsureTableExists` method
once by hand instead.

### Schema/script values to confirm before running

In `DB Scripts/register_scheduler_drainer.sql`:

- `@AssemblyName` should be `'Shared.Activities'` (already correct from dev).
- `@Cron` default `'*/5 * * * *'` — every 5 minutes. Change if you want a
  different cadence.
- `@TimeZoneId` default `'Arabian Standard Time'` — matches CrawlerServiceIndexing.
- `@Queue` default `'default'` — same queue Hangfire's main worker pool drains.

## 7. Designer — wire the activity onto the workflow

In Case Designer, open **Temporary Cash Payment**:

1. Add a **Code Activity** step **immediately before** the `Submit Original Invoices` user task.
2. Configure it to use the **existing class** `Shared.Activities.ScheduleHrNotificationActivity` from the deployed `Shared.Activities` assembly. **Do NOT paste source** — paste-source mode produces a dynamic in-memory assembly name (e.g. `wnxcv3cb.bmf`) that the Scheduler cannot resolve.
3. Save and publish the workflow.

The activity reads `DocumentId` automatically from `workflowItem.Properties` and
writes back two informational properties on success:
- `overdueNotifyRowId` — the inserted `dbo.CasePendingNotifications.Id`
- `overdueNotifyDueDate` — ISO timestamp when the drain will fire

Useful for downstream activities that want to cancel a pending reminder
(`UPDATE dbo.CasePendingNotifications SET Status='cancelled' WHERE Id = @id`).

## 8. IIS app-pool hardening (one-time, elevated PowerShell)

Hangfire's worker only runs while `w3wp` is alive. Out of the box, IIS idles
the app pool after 20 minutes of no HTTP traffic. Without this step, the drain
silently pauses any time the Portal goes quiet — and only wakes when someone
hits the site.

```powershell
Import-Module WebAdministration
Set-ItemProperty IIS:\AppPools\UC_CasePortal -Name processModel.idleTimeout       -Value "00:00:00"
Set-ItemProperty IIS:\AppPools\UC_CasePortal -Name recycling.periodicRestart.time -Value "00:00:00"
Restart-WebAppPool UC_CasePortal
```

After this w3wp stays alive 24/7 and Hangfire's cron fires reliably regardless
of user traffic.

## 9. Post-deploy verification

Run in this order against the production DB:

```sql
-- a) Recurring job is correctly registered
SELECT [Field], LEFT(CAST([Value] AS NVARCHAR(MAX)),200) AS Value
FROM   Scheduler.Hash
WHERE  [Key] = 'recurring-job:case-tcp-overdue-drainer'
ORDER  BY [Field];
-- Expect: 6 rows (CreatedAt, Cron, Job, Queue, TimeZoneId, V). NO Error field.
-- 'Job' value should read:
--   {"t":"Shared.Activities.ScheduleHrNotificationActivity, Shared.Activities","m":"Drain"}

-- b) Hangfire worker is heartbeating
SELECT Id, LastHeartbeat FROM Scheduler.Server;
-- Expect: at least one row, LastHeartbeat within the last minute.

-- c) Trigger a real workflow once, then confirm insert
SELECT * FROM dbo.CasePendingNotifications ORDER BY Id DESC;
-- Expect: a new row with FireAt = now + 7 days, Status='pending'.

-- d) Force-fire test: insert a row with FireAt in the past
INSERT INTO dbo.CasePendingNotifications (DocumentId, ActivityName, FireAt, Status)
VALUES (<an existing DocId>, 'Submit Original Invoices',
        DATEADD(MINUTE, -1, SYSUTCDATETIME()), 'pending');
-- Wait ~60 s, then:
SELECT TOP 5 * FROM dbo.CasePendingNotifications ORDER BY Id DESC;
-- Expect: that row's Status = 'sent' (task still open → email sent) OR
--                          = 'skipped' (task already closed) OR
--                          = 'failed' (with LastError populated).

-- e) Confirm Scheduler.Job records the fire
SELECT TOP 5 Id, StateName, CreatedAt
FROM   Scheduler.Job
WHERE  InvocationData LIKE '%ScheduleHrNotificationActivity%'
ORDER  BY Id DESC;
-- Expect: rows with StateName='Succeeded'.
```

Also tail the activity log — at `C:\Logs\Case\ScheduleHrNotificationActivity-<today>.log`
you should see:

```
[..] [INFO ] [..] DRAIN  claimed 1 due row(s).
[..] [INFO ] [..] Reminder email sent for DocumentId=...
```

…confirming the full path: cron → claim → task-status check → email → mark sent.

## 10. Rollback

| Step done | How to undo |
|---|---|
| DLL deployed | Delete `C:\Program Files\Intalio\UC_CasePortal\Shared.Activities.dll`, restart app pool. |
| `deps.json` patched | Restore from `Intalio.Case.Portal.deps.json.bak`, restart app pool. |
| Recurring job registered | `DELETE FROM Scheduler.Hash WHERE [Key]='recurring-job:case-tcp-overdue-drainer'; DELETE FROM Scheduler.[Set] WHERE [Key]='recurring-jobs' AND [Value]='case-tcp-overdue-drainer';` |
| Template inserted | `DELETE FROM NotificationTemplate WHERE Name='OnTemporaryCashInvoiceOverdue';` |
| Pending queue table | `DROP TABLE dbo.CasePendingNotifications;` (only after confirming no rows you need). |
| Designer workflow change | Re-publish the previous workflow version. |
| App-pool timeouts changed | Re-apply the prod defaults (typically `idleTimeout = "00:20:00"`, `periodicRestart.time = "1.05:00:00"`). |

---

## Non-obvious risks (read once, then keep on file)

- **deps.json is the gate.** A correct `Shared.Activities.dll` in the bin
  folder is useless if `Intalio.Case.Portal.deps.json` doesn't list it under
  both `targets[".NETCoreApp,Version=v8.0"]` AND `libraries`. The runtime's
  `AssemblyLoadContext` enforces this strictly in .NET 8.
- **Source-paste vs existing-class in Designer.** If the workflow step is set
  to "paste source", every recompile generates a new random assembly name
  (`h2y130yf.01k`, `wnxcv3cb.bmf`, etc.) that the Scheduler cannot resolve.
  The deployment ONLY works if the Designer references the on-disk
  `Shared.Activities.dll` via the existing-class picker.
- **Idle timeout.** A correctly registered Scheduler entry plus a healthy
  deps.json plus a correctly wired workflow will still drift silently if
  Hangfire's worker dies because IIS retires the idle app pool. Step 8 is
  mandatory for a self-sustaining drain.
- **Future activity edits.** When you change a `.cs` file under
  `Case Code Activity Classes\`, rebuild Shared.Activities.dll and re-run
  `deploy-Shared.Activities.ps1`. The Scheduler registration stays valid
  because the assembly name doesn't drift.
- **Portal upgrades wipe the patch.** A Case Portal installer overwrites
  `C:\Program Files\Intalio\UC_CasePortal\` entirely, deleting both
  `Shared.Activities.dll` and the `deps.json` patch. **Mandatory
  post-upgrade step: re-run `deploy-Shared.Activities.ps1`** (it's
  idempotent — safe even if you accidentally run it twice). Make this
  part of the documented Portal upgrade SOP so the deploy team can't
  forget it.
