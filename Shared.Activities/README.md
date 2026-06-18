# Shared.Activities — fixed-name DLL for Case activities

Builds the `Case Code Activity Classes/**/*.cs` tree into a single class
library with a stable assembly name (`Shared.Activities.dll`) so the
Hangfire `Scheduler.Hash` recurring-job registration in
`DB Scripts/register_scheduler_drainer.sql` resolves types by name
without breaking on every Designer recompile.

Pasting `.cs` source into the Designer's code-activity slot produces an
in-memory script assembly with a random name (e.g. `h2y130yf.01k`).
Hangfire's `Type.GetType` calls `Assembly.Load` on that name; there's no
file by that name on disk, so the load fails. This project sidesteps
that entirely by producing a real, named DLL the Portal loads at
startup.

---

## Build

From the repo root:

```powershell
dotnet build Shared.Activities\Shared.Activities.csproj -c Release
```

Output:

```
Shared.Activities\bin\Release\net8.0\Shared.Activities.dll
```

The DLL is small (~155 KB; only your activity classes). Portal-side
dependencies are referenced as `<Private>false</Private>` so they
aren't copied into the output — the Portal already ships them.

---

## Deploy (production)

Use the included **`deploy-Shared.Activities.ps1`**. It's idempotent and
re-runnable, designed to be the deploy team's single command after every
Portal install / upgrade.

```powershell
# 1) Bundle (ship both files together to the deploy team):
#       deploy-Shared.Activities.ps1
#       Shared.Activities.dll
#
# 2) On the target Portal machine, from an ELEVATED PowerShell prompt:
.\deploy-Shared.Activities.ps1
```

What the script does:
1. Pre-flight check — DLL present, Portal path exists, deps.json
   exists, app pool exists, Intalio.Case.Portal.Core version
   auto-detected from deps.json.
2. Stops the Case Portal app pool.
3. Backs up `Intalio.Case.Portal.deps.json` with a timestamp suffix.
4. Patches `deps.json` — adds `Shared.Activities/1.0.0.0` to both the
   `targets[".NETCoreApp,Version=v8.0"]` block and the `libraries`
   block. Idempotent (no duplicate entries if you re-run).
5. Validates the resulting JSON; auto-rolls back if the patch broke it.
6. Copies `Shared.Activities.dll` into the Portal bin folder.
7. Starts the app pool back up, waits for it to reach `Started`.
8. Prints the verification SQL.

**Override parameters** if your install differs:

```powershell
.\deploy-Shared.Activities.ps1 `
    -PortalPath  "D:\Apps\CasePortal" `
    -DllPath     ".\Shared.Activities.dll" `
    -AppPoolName "Intalio.Case"
```

---

## Why we patch deps.json

.NET 8 ASP.NET Core IIS apps only load assemblies listed in
`<app>.deps.json`. Dropping a DLL in the bin folder alone doesn't make
it discoverable by `AssemblyLoadContext.Default`. The Portal's
`AssemblyResolve` event isn't hooked for arbitrary load names either
(verified empirically — see `Hangfire.Common.TypeHelper.DefaultTypeResolver`
stack trace on a failed load), so the only path that works is to put
the entry in `deps.json` itself.

---

## Updating the activities later

Edit `.cs` files in `Case Code Activity Classes\`. Rebuild. Re-run the
deploy script. The Scheduler registration stays valid because the
assembly name doesn't drift.

```powershell
dotnet build Shared.Activities\Shared.Activities.csproj -c Release
# Then, on the Portal machine (elevated):
.\deploy-Shared.Activities.ps1
```

---

## Portal upgrade survival

Portal upgrades overwrite `C:\Program Files\Intalio\UC_CasePortal\`
entirely — that wipes both `Shared.Activities.dll` (from bin) and the
deps.json patch. The deploy script re-applies both in one command.

Adopt the script as a mandatory post-upgrade step:

```
Portal upgrade procedure:
    1. Run the Intalio Case Portal installer.
    2. Stop UC_CasePortal app pool.
    3. Run .\deploy-Shared.Activities.ps1
    4. Verify drain via the SQL in step 8 of the script's output.
```

If the deploy team can be relied on, step 3 is enough. The script is
idempotent — re-running it on a Portal that's already patched is
harmless.
