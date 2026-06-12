SET NOCOUNT ON;

DECLARE @Name         NVARCHAR(200) = N'OnCaseDocumentsForAction';
DECLARE @Subject      NVARCHAR(400) = N'[ReferenceNumber] - Documents for your action ([WorkflowName])';
DECLARE @BookmarkList NVARCHAR(MAX) = N'[ReferenceNumber],[WorkflowName],[DocumentId],[Date],[Time],[FromName]';

-- Pretty, table-based HTML body — renders well on Outlook, Gmail, Apple Mail.
DECLARE @Body NVARCHAR(MAX) = N'<!doctype html>
<html lang="en">
<head>
<meta http-equiv="Content-Type" content="text/html; charset=UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Case Documents</title>
</head>
<body style="margin:0;padding:0;background:#f4f6f8;font-family:Segoe UI,Arial,sans-serif;color:#333;">
  <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#f4f6f8;padding:24px 0;">
    <tr><td align="center">
      <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="600" style="background:#ffffff;border:1px solid #e1e5ea;border-radius:6px;overflow:hidden;">

        <!-- Header -->
        <tr><td style="background:#1f3a5f;padding:18px 24px;color:#ffffff;">
          <div style="font-size:18px;font-weight:600;">[WorkflowName]</div>
          <div style="font-size:13px;opacity:0.85;margin-top:4px;">Reference: <strong>[ReferenceNumber]</strong></div>
        </td></tr>

        <!-- Greeting + body -->
        <tr><td style="padding:24px;font-size:14px;line-height:1.6;color:#333;">
          <p style="margin:0 0 14px;">Dear team,</p>
          <p style="margin:0 0 14px;">Please find attached the documents for case <strong>[ReferenceNumber]</strong> for your action.</p>
          <p style="margin:0 0 14px;">Workflow: <strong>[WorkflowName]</strong></p>
          <p style="margin:0;">Kind regards,<br><span style="color:#777;">[FromName]</span></p>
        </td></tr>

        <!-- Call-out -->
        <tr><td style="padding:0 24px 24px;">
          <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#fff8e1;border:1px solid #f7d774;border-radius:4px;">
            <tr><td style="padding:12px 14px;font-size:13px;color:#5b4500;">
              <strong>Action required:</strong> kindly review the attached documents and reply once your review is complete.
            </td></tr>
          </table>
        </td></tr>

        <!-- Footer -->
        <tr><td style="background:#fafbfc;padding:12px 24px;font-size:11px;color:#999;border-top:1px solid #e1e5ea;">
          Generated on [Date] [Time] this is an automated message from the Case Portal.
        </td></tr>

      </table>
    </td></tr>
  </table>
</body>
</html>';

-- Idempotent upsert: insert if absent, update body/subject if already present.
IF NOT EXISTS (SELECT 1 FROM NotificationTemplate WHERE Name = @Name)
BEGIN
    INSERT INTO NotificationTemplate (Name, Subject, Body, CreatedByUserId, CreatedDate, BookmarkList, IsSystem)
    VALUES (@Name, @Subject, @Body, 1, SYSDATETIME(), @BookmarkList, 1);
    PRINT 'Inserted new NotificationTemplate row: ' + @Name;
END
ELSE
BEGIN
    UPDATE NotificationTemplate
       SET Subject = @Subject,
           Body = @Body,
           BookmarkList = @BookmarkList,
           ModifiedDate = SYSDATETIME()
     WHERE Name = @Name;
    PRINT 'Updated existing NotificationTemplate row: ' + @Name;
END

SELECT Id, Name, Subject, BookmarkList, IsSystem, CreatedDate, ModifiedDate
FROM   NotificationTemplate
WHERE  Name = @Name;
GO

-- ============================================================================
-- Template: OnTemporaryCashInvoiceOverdue
--   Sent by ScheduleHrNotificationActivity's drain loop when the "Submit
--   Original Invoices" task on a Temporary Cash Payment case stays open
--   past the deadline (default 7 days). Recipient is HR — the email is a
--   nudge to start the salary-deduction process for the missing invoices.
--
-- Bookmarks (all substituted by the activity's BuildBookmarks):
--   [ReferenceNumber] [WorkflowName] [DocumentId]
--   [TaskActivity]    [TaskId]       [TaskOpenedDate]  [TaskAgeDays]
--   [Date]            [Time]         [FromName]
-- ============================================================================

DECLARE @Name         NVARCHAR(200) = N'OnTemporaryCashInvoiceOverdue';
DECLARE @Subject      NVARCHAR(400) = N'[Overdue] Invoices not submitted - [ReferenceNumber] ([WorkflowName])';
DECLARE @BookmarkList NVARCHAR(MAX) =
    N'[ReferenceNumber],[WorkflowName],[DocumentId],[TaskActivity],[TaskId],[TaskOpenedDate],[TaskAgeDays],[Date],[Time],[FromName]';

-- Warning-toned variant of the OnCaseDocumentsForAction layout. Red
-- header band + amber call-out so it reads as an escalation, not a
-- routine attachment notice.
DECLARE @Body NVARCHAR(MAX) = N'<!doctype html>
<html lang="en">
<head>
<meta http-equiv="Content-Type" content="text/html; charset=UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Invoices Not Submitted</title>
</head>
<body style="margin:0;padding:0;background:#f4f6f8;font-family:Segoe UI,Arial,sans-serif;color:#333;">
  <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="background:#f4f6f8;padding:24px 0;">
    <tr><td align="center">
      <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="600" style="background:#ffffff;border:1px solid #e1e5ea;border-radius:6px;overflow:hidden;">

        <!-- Header -->
        <tr><td style="background:#b71c1c;padding:18px 24px;color:#ffffff;">
          <div style="font-size:18px;font-weight:600;">[WorkflowName] - Overdue</div>
          <div style="font-size:13px;opacity:0.9;margin-top:4px;">Reference: <strong>[ReferenceNumber]</strong></div>
        </td></tr>

        <!-- Greeting + body -->
        <tr><td style="padding:24px;font-size:14px;line-height:1.6;color:#333;">
          <p style="margin:0 0 14px;">Dear HR team,</p>
          <p style="margin:0 0 14px;">
            The original invoices for case <strong>[ReferenceNumber]</strong> have <strong>not been submitted</strong>
            within the agreed deadline.
          </p>

          <!-- Case details table -->
          <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%"
                 style="border-collapse:collapse;font-size:13px;margin:0 0 14px;">
            <tr>
              <td style="padding:6px 10px;border:1px solid #e1e5ea;background:#fafbfc;width:40%;color:#555;">Workflow</td>
              <td style="padding:6px 10px;border:1px solid #e1e5ea;"><strong>[WorkflowName]</strong></td>
            </tr>
            <tr>
              <td style="padding:6px 10px;border:1px solid #e1e5ea;background:#fafbfc;color:#555;">Reference number</td>
              <td style="padding:6px 10px;border:1px solid #e1e5ea;"><strong>[ReferenceNumber]</strong></td>
            </tr>
            <tr>
              <td style="padding:6px 10px;border:1px solid #e1e5ea;background:#fafbfc;color:#555;">Pending task</td>
              <td style="padding:6px 10px;border:1px solid #e1e5ea;">[TaskActivity]</td>
            </tr>
            <tr>
              <td style="padding:6px 10px;border:1px solid #e1e5ea;background:#fafbfc;color:#555;">Task opened</td>
              <td style="padding:6px 10px;border:1px solid #e1e5ea;">[TaskOpenedDate] ([TaskAgeDays] day(s) ago)</td>
            </tr>
          </table>

          <p style="margin:0;">Kind regards,<br><span style="color:#777;">[FromName]</span></p>
        </td></tr>

        <!-- Call-out -->
        <tr><td style="padding:0 24px 24px;">
          <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%"
                 style="background:#fff8e1;border:1px solid #f7d774;border-radius:4px;">
            <tr><td style="padding:12px 14px;font-size:13px;color:#5b4500;">
              <strong>Action required:</strong> per policy, kindly proceed with the salary deduction
              for the receiver and follow up on the missing invoices.
            </td></tr>
          </table>
        </td></tr>

        <!-- Footer -->
        <tr><td style="background:#fafbfc;padding:12px 24px;font-size:11px;color:#999;border-top:1px solid #e1e5ea;">
          Generated on [Date] [Time] - this is an automated message from the Case Portal.
        </td></tr>

      </table>
    </td></tr>
  </table>
</body>
</html>';

-- Idempotent upsert: insert if absent, update body/subject if already present.
IF NOT EXISTS (SELECT 1 FROM NotificationTemplate WHERE Name = @Name)
BEGIN
    INSERT INTO NotificationTemplate (Name, Subject, Body, CreatedByUserId, CreatedDate, BookmarkList, IsSystem)
    VALUES (@Name, @Subject, @Body, 1, SYSDATETIME(), @BookmarkList, 1);
    PRINT 'Inserted new NotificationTemplate row: ' + @Name;
END
ELSE
BEGIN
    UPDATE NotificationTemplate
       SET Subject = @Subject,
           Body = @Body,
           BookmarkList = @BookmarkList,
           ModifiedDate = SYSDATETIME()
     WHERE Name = @Name;
    PRINT 'Updated existing NotificationTemplate row: ' + @Name;
END

SELECT Id, Name, Subject, BookmarkList, IsSystem, CreatedDate, ModifiedDate
FROM   NotificationTemplate
WHERE  Name = @Name;
GO
