# Shared.Activities — fixed-name DLL for Case activities

Builds the `Case Code Activity Classes/**/*.cs` tree into a single class
library DLL with a stable assembly name (`Shared.Activities.dll`), so the
Hangfire `Scheduler.Hash` recurring-job registration in
`DB Scripts/register_scheduler_drainer.sql` can resolve types by name
without breaking on every Designer recompile.

Pasting `.cs` source into the Designer's code-activity slot produces an
in-memory script assembly with a random name (e.g. `h2y130yf.01k`).
Hangfire's `Type.GetType` calls `Assembly.Load` on that name; there's no
file by that name on disk so the load fails. This project sidesteps
that entirely.

---

## Build

From the repo root:

```powershell
dotnet build "Shared.Activities\Shared.Activities.csproj" -c Release
```

Output:

```
Shared.Activities\bin\Release\net8.0\Shared.Activities.dll
```

The DLL is small (only your activity classes; Portal-side dependencies
are referenced as `<Private>false</Private>` so they're not copied into
the output).

## Deploy

1. Stop the Case Portal (so the bin folder is writable):
   - If hosted in IIS: `iisreset /stop`, or recycle the app pool
   - If a standalone exe: stop the process

2. Copy the DLL into the Portal's bin folder:

   ```powershell
   Copy-Item `
     "Shared.Activities\bin\Release\net8.0\Shared.Activities.dll" `
     "C:\Program Files\Intalio\UC_CasePortal\Shared.Activities.dll" `
     -Force
   ```

   (The exact bin path on your machine is `C:\Program Files\Intalio\UC_CasePortal\` — the same folder as `Intalio.Case.Portal.dll` and all the other Portal assemblies. Adjust if your install lives elsewhere.)

3. Start the Portal again.

## Wire up to the Designer

After deploying the DLL **once**, the Designer's code-activity picker
should browse the loaded assembly and let you select any class instead
of pasting source. Configuration varies slightly per Designer version,
but the property panel typically has one of:

- **"Existing class"** radio button → pick `Shared.Activities.<Type>` from a tree, OR
- **"Class name"** text field → type the fully qualified name `Shared.Activities.ScheduleHrNotificationActivity` and `Shared.Activities` for assembly

Once the Designer references the on-disk DLL instead of compiling fresh
source, `typeof(Activity).Assembly.GetName().Name` permanently returns
`Shared.Activities`.

## Update the Scheduler registration

Open `DB Scripts/register_scheduler_drainer.sql` and change:

```sql
DECLARE @AssemblyName NVARCHAR(500) = N'h2y130yf.01k';
```

…to:

```sql
DECLARE @AssemblyName NVARCHAR(500) = N'Shared.Activities';
```

Then re-run the script. The MERGE updates the existing `Job` field in
`Scheduler.Hash` to:

```json
{"t":"Shared.Activities.ScheduleHrNotificationActivity, Shared.Activities","m":"Drain"}
```

Also clear the stale failure state from the previous run:

```sql
DELETE FROM Scheduler.Hash
WHERE  [Key] = 'recurring-job:case-tcp-overdue-drainer'
  AND  [Field] IN ('Error','RetryAttempt','NextExecution');

UPDATE Scheduler.[Set]
   SET [Score] = DATEDIFF_BIG(SECOND, '1970-01-01', GETUTCDATE())
 WHERE [Key]   = 'recurring-jobs'
   AND [Value] = 'case-tcp-overdue-drainer';
```

Hangfire picks it up within a few seconds — verify per the diagnosis
matrix in the activity's log file `C:\Logs\Case\ScheduleHrNotificationActivity-<today>.log`.

## Updating the activities later

Edit the `.cs` files in `Case Code Activity Classes\`. Re-build. Stop
Portal. Copy the new `Shared.Activities.dll` over the old one. Start
Portal. The Scheduler registration stays intact because the assembly
name didn't change.
