-- =============================================================================
-- Restore the Probation Evaluation workflow's form JSON from a previous
-- version row in WorkflowDefinition.
--
-- Background: Intalio keeps every published workflow version as its own row.
-- When a publish accidentally wipes the FormInputDesigner column, the previous
-- version's row still has the intact JSON. This script copies the JSON (both
-- FormInputDesigner and FormInputDesignerTranslation) from the source row
-- into the current active row.
--
-- HOW TO USE:
--   1. Verify the @sourceVersionId / @targetVersionId values below match
--      what you saw in the discovery query (the previous intact one vs the
--      current broken one).
--   2. Run the script. It writes a timestamped "snapshot" row first so the
--      current broken state is recoverable if needed.
--   3. Recycle the Portal app pool so the new JSON is loaded (the Designer
--      caches workflow defs in memory).
-- =============================================================================

SET NOCOUNT ON;

-- ----- STEP 1 - Pick the versions ------------------------------------------

DECLARE @sourceVersionId INT = 574;  -- the version with the intact form JSON
DECLARE @targetVersionId INT = 576;  -- the current active version that's broken

-- ----- STEP 2 - Sanity check both rows exist and source has real data ------

DECLARE @srcLen INT = (SELECT LEN(CAST(FormInputDesigner AS NVARCHAR(MAX))) FROM WorkflowDefinition WHERE WorkflowId = @sourceVersionId);
DECLARE @tgtLen INT = (SELECT LEN(CAST(FormInputDesigner AS NVARCHAR(MAX))) FROM WorkflowDefinition WHERE WorkflowId = @targetVersionId);

IF @srcLen IS NULL OR @srcLen < 1000
BEGIN
    PRINT 'ABORT: source WorkflowId ' + CAST(@sourceVersionId AS NVARCHAR(10)) +
          ' does not have a real form JSON (length=' + ISNULL(CAST(@srcLen AS NVARCHAR(10)),'NULL') +
          '). Pick a different source.';
    RETURN;
END
IF @tgtLen IS NULL
BEGIN
    PRINT 'ABORT: target WorkflowId ' + CAST(@targetVersionId AS NVARCHAR(10)) +
          ' does not exist.';
    RETURN;
END

PRINT 'Source WorkflowId=' + CAST(@sourceVersionId AS NVARCHAR(10)) + '  form JSON length=' + CAST(@srcLen AS NVARCHAR(20));
PRINT 'Target WorkflowId=' + CAST(@targetVersionId AS NVARCHAR(10)) + '  current form JSON length=' + CAST(@tgtLen AS NVARCHAR(20));

-- ----- STEP 3 - Snapshot the current (broken) state for rollback -----------
-- We write the current FormInputDesigner content to a snapshot row in a
-- side table so the broken state can be recovered later if needed.

IF OBJECT_ID('dbo.WorkflowFormJsonBackup', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WorkflowFormJsonBackup (
        Id                            BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        WorkflowId                    INT          NOT NULL,
        SnapshotAt                    DATETIME2    NOT NULL CONSTRAINT DF_WFJB_SnapshotAt DEFAULT (SYSUTCDATETIME()),
        FormInputDesigner             NVARCHAR(MAX) NULL,
        FormInputDesignerTranslation  NVARCHAR(MAX) NULL,
        Note                          NVARCHAR(400) NULL
    );
    PRINT 'Created backup table dbo.WorkflowFormJsonBackup.';
END

INSERT INTO dbo.WorkflowFormJsonBackup (WorkflowId, FormInputDesigner, FormInputDesignerTranslation, Note)
SELECT WorkflowId, FormInputDesigner, FormInputDesignerTranslation,
       'pre-restore snapshot of WorkflowId ' + CAST(@targetVersionId AS NVARCHAR(10)) +
       ' before copying form from WorkflowId ' + CAST(@sourceVersionId AS NVARCHAR(10))
FROM   WorkflowDefinition
WHERE  WorkflowId = @targetVersionId;

PRINT 'Snapshot row written - Id=' + CAST(SCOPE_IDENTITY() AS NVARCHAR(20));

-- ----- STEP 4 - Restore form JSON + translations ---------------------------

UPDATE T
SET    T.FormInputDesigner             = S.FormInputDesigner,
       T.FormInputDesignerTranslation  = S.FormInputDesignerTranslation
FROM   WorkflowDefinition T
CROSS JOIN WorkflowDefinition S
WHERE  T.WorkflowId = @targetVersionId
  AND  S.WorkflowId = @sourceVersionId;

PRINT 'Restored. Rows affected: ' + CAST(@@ROWCOUNT AS NVARCHAR(10));

-- ----- STEP 5 - Verify -----------------------------------------------------

SELECT  WorkflowId,
        IsClosed,
        LEN(CAST(FormInputDesigner AS NVARCHAR(MAX)))            AS FormJsonLen,
        LEN(CAST(FormInputDesignerTranslation AS NVARCHAR(MAX))) AS FormTrLen
FROM    WorkflowDefinition
WHERE   WorkflowId IN (@sourceVersionId, @targetVersionId)
ORDER   BY WorkflowId;

PRINT '';
PRINT 'Next step: recycle the Portal app pool so the Designer reloads the new form JSON.';
PRINT '  Restart-WebAppPool UC_CasePortal   # elevated PowerShell';
