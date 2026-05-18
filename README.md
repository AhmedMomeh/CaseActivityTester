# Case Activity Tester

Custom `ActivityTemplate` classes used by Intalio UC Case Portal workflows,
plus a console test harness that lets us run any activity against the live
`UC_Case` database without going through the Designer.

## Layout

```
ActivityTester/
├── ActivityTester.csproj          Test harness project
├── ActivityTester.sln             Solution file
├── Program.cs                     Entry point — pick which activity + workflow properties
├── GlobalUsings.cs                global using Task = System.Threading.Tasks.Task;
├── Run.cmd                        dotnet build && dotnet run
│
└── Case Code Activity Classes/    The activities — paste any of these into Case
    ├── Common/                    Designer Code Activity Templates as ClassType.Data.
    │   └── BuildApprovalHistoryActivity.cs   Builds the approval-history table on each approval.
    ├── Stamp Documents/
    │   └── StampApprovedDocumentsActivity.cs Stamps "APPROVED" image on PDF/DOCX attachments.
    ├── Admin/                     Procurement workflows: IPO, LPO, RFQ routing activities.
    ├── HR/                        HR workflows: Job Description, Candidate Selection, etc.
    └── Not used/                  Old activities kept for reference.
```

## Build & run

```cmd
dotnet build -c Debug
dotnet run --no-build -c Debug
```

Or just double-click `Run.cmd`.

The .NET SDK auto-includes every `.cs` under the project folder, so any file
you drop into `Case Code Activity Classes/` is compiled into the harness on
the next build.

## Editing activities

Edit any `.cs` file under `Case Code Activity Classes/` directly. The
test harness re-compiles them on `dotnet build`. When ready to deploy,
copy the class body into the corresponding Code Activity Template in
Case Designer (paste into `ClassType.Data` of the activity).

## Logging

Each activity writes a daily-rotated log file under `C:\IntalioLogs\`,
for example `BuildApprovalHistoryActivity-YYYY-MM-DD.log`.

## Dependencies

The harness references Intalio runtime assemblies from the local Portal
install at `C:\Program Files\Intalio\UC_CasePortal` (set in the csproj
as `<PortalDir>`). Adjust if your Portal lives elsewhere.

## Known quirk

`Case Code Activity Classes\HR\Short listed Candidate List\ArchiveDocumentsToDMSActivity.cs`
is byte-identical to the copy under `HR\Candidate Selection Form\` and would
collide on class name. The csproj has a `<Compile Remove>` for the SLC copy
so the harness builds — either delete one or rename the class to drop that
exclusion.
