-- =============================================================================
-- Register the 'case-tcp-overdue-drainer' recurring job in Intalio's
-- Scheduler tables (Scheduler.Hash + Scheduler.[Set]).
--
-- Schema mirrors Hangfire 1.7 with Intalio's two customisations:
--   - Job descriptor JSON uses short keys: {"t":"<type, asm>","m":"<method>"}
--   - Versioning field 'V' = 2
-- Verified by reading the CrawlerServiceIndexing recurring job:
--   recurring-job:CrawlerServiceIndexing  Job  {"t":"Intalio.Case.Portal.Services.CrawlingService, Intalio.Case.Portal","m":"CrawlData"}
--
-- Run ONCE against the Case database. Safe to re-run — the MERGE is idempotent.
-- =============================================================================

-- =============================================================================
-- STEP 1 - Find the assembly name your activities compile to.
--
-- The activity now logs its own assembly name on every Execute. Trigger any
-- Temporary Cash Payment workflow once, then open:
--   C:\Logs\Case\ScheduleHrNotificationActivity-<today>.log
-- and look for the line:
--   [..] [INFO ] [tid=..] ---- BEGIN  DocumentId=..  assembly='<NAME>' ----
-- Copy whatever appears between the quotes.
-- =============================================================================

DECLARE @AssemblyName NVARCHAR(500) = N'Shared.Activities';
-- Fixed value matching Shared.Activities.dll. Build with:
--     dotnet build Shared.Activities\Shared.Activities.csproj -c Release
-- and copy bin\Release\net8.0\Shared.Activities.dll into the Portal's bin
-- folder BEFORE running this script. After that, the assembly name is
-- stable across rebuilds and you never need to touch this line again.
--
-- If you're still on Designer source-paste mode (no DLL deployed yet),
-- the value is whatever your activity log shows between the single quotes
-- in the line:
--   ---- BEGIN  DocumentId=..  assembly='...' ----
-- Replace 'Shared.Activities' above with that name; expect to rerun after
-- every Portal restart since the random name changes.

-- =============================================================================
-- STEP 2 - Tweak these if you want a different schedule / queue / time zone.
-- =============================================================================

DECLARE @JobId      NVARCHAR(100) = N'case-tcp-overdue-drainer';
DECLARE @Cron       NVARCHAR(50)  = N'*/5 * * * *';            -- every 5 minutes
DECLARE @TimeZoneId NVARCHAR(100) = N'Arabian Standard Time';  -- same as CrawlerServiceIndexing
DECLARE @Queue      NVARCHAR(50)  = N'default';                -- CrawlerServiceIndexing uses
                                                               -- 'portalcrawler' on its own
                                                               -- pool; the Portal almost
                                                               -- always also runs 'default'.
DECLARE @V          NVARCHAR(10)  = N'2';

-- =============================================================================
-- STEP 3 - Build the recurring-job hash payload.
-- =============================================================================

-- {"t":"Shared.Activities.ScheduleHrNotificationActivity, <asm>","m":"Drain"}
DECLARE @JobJson NVARCHAR(MAX) =
    N'{"t":"Shared.Activities.ScheduleHrNotificationActivity, ' + @AssemblyName +
    N'","m":"Drain"}';

DECLARE @HashKey NVARCHAR(200) = N'recurring-job:' + @JobId;

-- Hangfire stores CreatedAt as Unix ms (epoch milliseconds, UTC).
-- The Score in [Set] is Unix SECONDS for NextExecution — using "now" lets the
-- scheduler fire as soon as it picks up the row on its next poll, then it
-- self-corrects to the cron afterward.
DECLARE @NowMs   BIGINT = DATEDIFF_BIG(MILLISECOND, '1970-01-01', GETUTCDATE());
DECLARE @NowSecs BIGINT = @NowMs / 1000;

-- =============================================================================
-- STEP 4 - Upsert the Scheduler.Hash fields.
-- =============================================================================

MERGE INTO Scheduler.Hash AS T
USING (VALUES
    (@HashKey, N'CreatedAt',  CAST(@NowMs AS NVARCHAR(50))),
    (@HashKey, N'Cron',       @Cron),
    (@HashKey, N'Job',        @JobJson),
    (@HashKey, N'Queue',      @Queue),
    (@HashKey, N'TimeZoneId', @TimeZoneId),
    (@HashKey, N'V',          @V)
) AS S([Key], [Field], [Value])
ON  T.[Key] = S.[Key] AND T.[Field] = S.[Field]
WHEN MATCHED     THEN UPDATE SET [Value] = S.[Value]
WHEN NOT MATCHED THEN INSERT ([Key], [Field], [Value])
                       VALUES (S.[Key], S.[Field], S.[Value]);

-- =============================================================================
-- STEP 5 - Add the job id to the 'recurring-jobs' Set with NextExecution score.
-- =============================================================================

IF NOT EXISTS (
    SELECT 1 FROM Scheduler.[Set]
    WHERE [Key] = N'recurring-jobs' AND [Value] = @JobId)
BEGIN
    INSERT INTO Scheduler.[Set] ([Key], [Score], [Value])
    VALUES (N'recurring-jobs', @NowSecs, @JobId);
END
ELSE
BEGIN
    -- Already registered earlier — bump NextExecution to now so the scheduler
    -- picks the (possibly updated) cron/job JSON up immediately.
    UPDATE Scheduler.[Set]
       SET [Score] = @NowSecs
     WHERE [Key] = N'recurring-jobs' AND [Value] = @JobId;
END

-- =============================================================================
-- STEP 6 - Verify. Both sections must return rows.
-- =============================================================================

SELECT 'Hash entries' AS Section, [Field], [Value]
FROM   Scheduler.Hash
WHERE  [Key] = @HashKey;

SELECT 'Set entry' AS Section, [Key], [Score], [Value]
FROM   Scheduler.[Set]
WHERE  [Key] = N'recurring-jobs' AND [Value] = @JobId;
