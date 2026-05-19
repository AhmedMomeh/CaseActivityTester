using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using Intalio.Core.Utility;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;

namespace Shared.Activities
{
    /// <summary>
    /// After each approval task closes, this activity rebuilds the "Approval History"
    /// page inside the case's generated PDF attachment in Storage.
    ///
    /// Per approval task that took an "Approve" transition, one row is added with:
    ///   Approved By  |  Department  |  Date
    ///
    /// Rejected tasks are excluded.  Each run wipes any prior approval page this
    /// activity wrote and rebuilds from the live task log, so the table is always
    /// current and the activity is fully idempotent.
    /// </summary>
    public class BuildApprovalHistoryActivity : ActivityTemplate
    {
        // -------- Config --------

        #region Deveploment
        private const string IamBaseUrl        = "http://localhost:11111";
        private const string StorageBaseUrl    = "http://localhost:44444/";
        private const string AuthCaseClientId      = "42faa0d5-9856-4639-9d73-36a5fb6bb561";
        private const string AuthCaseClientSecret  = "5b83f635-35d6-4d87-bda4-bbd471ce26c2";
        private const string AuthUserName      = "admin";
        private const string AuthUserPassword  = "1";
        private const string LogDirectory = @"C:\Logs\Case";
        #endregion

        #region Staging
        //private const string IamBaseUrl = "http://uciamdev.unioncoop.ae";       
        //private const string StorageBaseUrl = "http://ucstoragedev.unioncoop.ae/";
        //private const string AuthCaseClientId = "840fe558-9085-4d8c-ba81-9e442fde7409";
        //private const string AuthCaseClientSecret = "4c6f2153-69af-490e-a6b5-8e928048500e";
        //private const string AuthUserName = "admin";
        //private const string AuthUserPassword = "1";
        //private const string LogDirectory = @"C:\Logs\Case";
        #endregion

        #region Production
        //private const string IamBaseUrl = "https://uciam.unioncoop.ae";       
        //private const string StorageBaseUrl = "https://ucstorage.unioncoop.ae/";
        //private const string AuthCaseClientId = "840fe558-9085-4d8c-ba81-9e442fde7409";
        //private const string AuthCaseClientSecret = "4c6f2153-69af-490e-a6b5-8e928048500e";
        //private const string AuthUserName = "admin";
        //private const string AuthUserPassword = "1"; 
        //private const string LogDirectory = @"C:\Logs\Case";
        #endregion



        // Visible heading text used both as the section title AND as the marker
        // that lets us identify and remove our own prior page on re-runs.
        private const string ApprovalPageMarker = "Approval History";
        
        private static readonly object LogLock = new object();
        private static int _licenseApplied;

        private sealed class Approval
        {
            public long   UserId;
            public string Name;
            public string Department;
            public string Date;            // yyyy-MM-dd
            public long?  StructureId;
        }

        private sealed class AttachmentRef
        {
            public long   Id;
            public string Name;
            public string StorageAttachmentId;
            public string Ext;          // ".pdf" or ".docx"
            public string ContentType;  // matching mime for Storage upload
        }

        public override void Complete(WorkflowItem workflowItem) { }

        public override void Execute(WorkflowItem workflowItem)
        {
            string docIdStr = GetProp(workflowItem, "DocumentId");
            LogInfo($"---- BEGIN  DocumentId={docIdStr} ----");
            if (!long.TryParse(docIdStr, out long documentId) || documentId <= 0)
            {
                LogError($"Invalid DocumentId: '{docIdStr}'");
                return;
            }

            try
            {
                EnsureAsposeLicensed();

                // 1. Load completed approvals (Approve transitions only) for this document.
                var approvals = LoadApprovals(documentId);
                LogInfo($"Loaded {approvals.Count} approval(s) from task history.");
                if (approvals.Count == 0) { LogInfo("Nothing to render."); return; }

                // 2. Find the case's main PDF attachment.
                var att = FindMainAttachment(documentId);
                if (att == null) { LogWarn($"No PDF attachment found on document {documentId} — aborting."); return; }
                LogInfo($"Target attachment id={att.Id} name='{att.Name}' storageId='{att.StorageAttachmentId}'");

                // 3. Resolve approver display names + department names via IAM.
                string token = GetTokenAsync().GetAwaiter().GetResult();
                ResolveApproverNamesAndDepartments(approvals, token);

                // 4. Update Document.Form.approvalHistory so the form's editGrid shows the
                //    same list under the Application Metadata tab.
                UpdateFormApprovalHistory(documentId, approvals);

                // 5. Download the file, rewrite per format (.pdf or .docx), upload back.
                byte[] inBytes  = DownloadAsync(att.StorageAttachmentId, token).GetAwaiter().GetResult();
                byte[] outBytes = (att.Ext == ".pdf")
                    ? RewriteApprovalSectionPdf(inBytes, approvals)
                    : RewriteApprovalSectionDocx(inBytes, approvals);
                ReplaceAsync(att.StorageAttachmentId, att.Name, att.ContentType, outBytes, token)
                    .GetAwaiter().GetResult();

                LogInfo($"Wrote {approvals.Count} row(s) — pdfBytes={inBytes.Length}->{outBytes.Length}");
                LogInfo($"---- END    DocumentId={documentId}  result=success ----");
            }
            catch (Exception ex)
            {
                LogException("Execute() failed", ex);
                LogInfo($"---- END    DocumentId={docIdStr}  result=FAILED ----");
                throw;
            }
        }

        // ---------------------------------------------------------------------
        // Approval data loader — single SQL query, returns only Approve transitions.
        // ---------------------------------------------------------------------
        private static List<Approval> LoadApprovals(long documentId)
        {
            var list = new List<Approval>();
            string conn = Intalio.Case.Core.Configuration.DbConnectionString;
            if (string.IsNullOrEmpty(conn)) { LogWarn("No DB connection string."); return list; }

            const string sql = @"
WITH X AS (
    SELECT
        t.Id          AS TaskId,
        t.OwnerUserId AS UserId,
        t.ClosedDate  AS ClosedDate,
        t.StructureId AS StructureId,
        curr_ai.ActivityDefinitionId AS FromActId,
        (SELECT TOP 1 ai.ActivityDefinitionId
         FROM   ActivityInstances ai
         WHERE  ai.WorkflowInstanceId = curr_ai.WorkflowInstanceId
           AND  ai.StartDate          > t.ClosedDate
         ORDER  BY ai.StartDate)       AS ToActId
    FROM   Task t
    JOIN   ActivityInstances curr_ai ON curr_ai.ActivityInstanceId = t.ActivityInstanceId
    WHERE  t.DocumentId  = @docId
      AND  t.ClosedDate  IS NOT NULL
      AND  t.OwnerUserId IS NOT NULL)
SELECT X.UserId, X.ClosedDate, X.StructureId, tr.Name
FROM   X
LEFT   JOIN Transitions tr ON tr.CurrentActivityId = X.FromActId AND tr.NextActivityId = X.ToActId
WHERE  tr.Name = 'Approve'
ORDER  BY X.ClosedDate;";

            using (var c = new SqlConnection(conn))
            {
                c.Open();
                using (var cmd = new SqlCommand(sql, c))
                {
                    cmd.Parameters.AddWithValue("@docId", documentId);
                    using (var rd = cmd.ExecuteReader())
                        while (rd.Read())
                            list.Add(new Approval
                            {
                                UserId      = rd.GetInt64(0),
                                Date        = rd.GetDateTime(1).ToString("yyyy-MM-dd"),
                                StructureId = rd.IsDBNull(2) ? (long?)null : rd.GetInt64(2)
                            });
                }
            }
            return list;
        }

        // ---------------------------------------------------------------------
        // Document.Form update — keeps the form's editGrid in sync with the PDF.
        //
        // The form's editGrid is keyed to "approvalHistory" and renders each row with
        // these inner keys: approvedBy, department, approvalDate.  We write the same
        // shape directly into the Document.Form JSON column so the next form load
        // displays the table.
        // ---------------------------------------------------------------------
        private static void UpdateFormApprovalHistory(long documentId, List<Approval> approvals)
        {
            string conn = Intalio.Case.Core.Configuration.DbConnectionString;
            if (string.IsNullOrEmpty(conn)) { LogWarn("No DB connection string — skipping Document.Form update."); return; }

            var array = new JArray();
            foreach (var a in approvals)
                array.Add(new JObject
                {
                    ["approvedBy"]   = a.Name       ?? "",
                    ["department"]   = a.Department ?? "",
                    ["approvalDate"] = a.Date       ?? ""
                });

            try
            {
                string current = null;
                using (var c = new SqlConnection(conn))
                {
                    c.Open();
                    using (var cmd = new SqlCommand("SELECT Form FROM Document WHERE Id = @id", c))
                    {
                        cmd.Parameters.AddWithValue("@id", documentId);
                        var v = cmd.ExecuteScalar();
                        current = v == null || v == DBNull.Value ? null : Convert.ToString(v);
                    }

                    JObject form;
                    if (string.IsNullOrWhiteSpace(current)) form = new JObject();
                    else { try { form = JObject.Parse(current); } catch { form = new JObject(); } }
                    form["approvalHistory"] = array;

                    using (var cmd = new SqlCommand("UPDATE Document SET Form = @form WHERE Id = @id", c))
                    {
                        cmd.Parameters.AddWithValue("@form", form.ToString(Formatting.None));
                        cmd.Parameters.AddWithValue("@id",   documentId);
                        cmd.ExecuteNonQuery();
                    }
                }
                LogInfo($"Document.Form.approvalHistory updated ({array.Count} row(s)).");
            }
            catch (Exception ex) { LogException($"UpdateFormApprovalHistory({documentId}) failed", ex); }
        }

        // ---------------------------------------------------------------------
        // Attachment lookup — the case's main generated PDF.
        // ---------------------------------------------------------------------
        private static AttachmentRef FindMainAttachment(long documentId)
        {
            // ListOriginalDocumentByDocumentId returns original + additional attachments.
            // We want the original auto-generated document (PDF or DOCX).
            var atts = new Intalio.Case.Portal.Core.DAL.Attachment().ListOriginalDocumentByDocumentId(documentId);
            if (atts == null) return null;
            foreach (var a in atts)
            {
                if (a == null || string.IsNullOrEmpty(a.StorageAttachmentId)) continue;
                string ext = (Path.GetExtension(a.Name) ?? "").ToLowerInvariant();
                string contentType = ext switch
                {
                    ".pdf"  => "application/pdf",
                    ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    _       => null
                };
                if (contentType == null) continue;
                return new AttachmentRef
                {
                    Id                  = a.Id,
                    Name                = a.Name,
                    StorageAttachmentId = a.StorageAttachmentId,
                    Ext                 = ext,
                    ContentType         = contentType
                };
            }
            return null;
        }

        // ---------------------------------------------------------------------
        // IAM — single GET per unique userId, cached for this run.
        // ---------------------------------------------------------------------
        private void ResolveApproverNamesAndDepartments(List<Approval> approvals, string token)
        {
            var userCache = new Dictionary<long, JObject>();
            foreach (var a in approvals)
            {
                JObject u;
                if (!userCache.TryGetValue(a.UserId, out u))
                {
                    u = FetchUser(a.UserId, token);
                    userCache[a.UserId] = u;
                }
                a.Name       = ResolveName(u, a.UserId);
                a.Department = ResolveDepartment(u, a.StructureId);
            }
        }

        private static JObject FetchUser(long userId, string token)
        {
            try
            {
                using (var client = new HttpClient())
                using (var req    = new HttpRequestMessage(HttpMethod.Get,
                                        IamBaseUrl.TrimEnd('/') + "/Api/GetUser?id=" + userId))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using (var resp = client.SendAsync(req).GetAwaiter().GetResult())
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            LogWarn($"GetUser({userId}) -> {(int)resp.StatusCode}");
                            return null;
                        }
                        return JObject.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    }
                }
            }
            catch (Exception ex) { LogException($"FetchUser({userId}) failed", ex); return null; }
        }

        private static string ResolveName(JObject user, long fallbackUserId)
        {
            if (user == null) return "User " + fallbackUserId;
            string full = (string)user["fullName"];
            if (!string.IsNullOrWhiteSpace(full)) return full.Trim();
            return ((string)user["firstName"] + " " + (string)user["lastName"]).Trim();
        }

        private static string ResolveDepartment(JObject user, long? taskStructureId)
        {
            if (user == null) return "";
            var structures = user["structures"] as JArray;
            if (structures == null || structures.Count == 0) return "";

            // 1) Structure the task was assigned to.
            if (taskStructureId.HasValue)
                foreach (var s in structures)
                    if ((long?)s["id"] == taskStructureId.Value)
                    {
                        var n = (string)s["name"];
                        if (!string.IsNullOrWhiteSpace(n)) return n;
                    }

            // 2) User's default structure.
            long? defStruct = (long?)user["defaultStructureId"];
            if (defStruct.HasValue)
                foreach (var s in structures)
                    if ((long?)s["id"] == defStruct.Value)
                    {
                        var n = (string)s["name"];
                        if (!string.IsNullOrWhiteSpace(n)) return n;
                    }

            // 3) First structure.
            return (string)structures[0]["name"] ?? "";
        }

        // ---------------------------------------------------------------------
        // PDF rewrite — remove any prior approval page, append a fresh one.
        //
        // We always render the approval section on a new appended page rather than
        // trying to slot it below existing content on the last existing page.
        // Reason: the page Portal generated has its content in absolute-position
        // page-content streams (not Aspose's paragraph collection), and overlaying
        // a FloatingBox onto such pages renders inconsistently or not at all.
        // ---------------------------------------------------------------------
        private static byte[] RewriteApprovalSectionPdf(byte[] pdfBytes, List<Approval> approvals)
        {
            using (var inMs  = new MemoryStream(pdfBytes))
            using (var outMs = new MemoryStream())
            using (var pdf   = new Aspose.Pdf.Document(inMs))
            {
                RemovePriorApprovalPages(pdf);
                AppendApprovalPage(pdf, approvals);
                pdf.OptimizeResources();
                pdf.Save(outMs);
                return outMs.ToArray();
            }
        }

        private static void RemovePriorApprovalPages(Aspose.Pdf.Document pdf)
        {
            int removed = 0;
            for (int i = pdf.Pages.Count; i >= 1; i--)
            {
                var absorber = new Aspose.Pdf.Text.TextAbsorber();
                pdf.Pages[i].Accept(absorber);
                string text = absorber.Text ?? "";
                if (text.IndexOf(ApprovalPageMarker, StringComparison.Ordinal) >= 0)
                {
                    pdf.Pages.Delete(i);
                    removed++;
                }
            }
            if (removed > 0) LogInfo($"Removed {removed} prior approval page(s).");
        }

        private static void AppendApprovalPage(Aspose.Pdf.Document pdf, List<Approval> approvals)
        {
            var page = pdf.Pages.Add();
            // Tighter top margin so the section starts near the top of the new page,
            // minimizing the perceived "page break gap" after the previous page.
            page.PageInfo.Margin = new Aspose.Pdf.MarginInfo(40, 24, 40, 40);

            var heading = new Aspose.Pdf.Text.TextFragment(ApprovalPageMarker);
            heading.TextState.FontSize  = 14;
            heading.TextState.FontStyle = Aspose.Pdf.Text.FontStyles.Bold;
            page.Paragraphs.Add(heading);

            var spacer = new Aspose.Pdf.Text.TextFragment(" ");
            spacer.TextState.FontSize = 4;
            page.Paragraphs.Add(spacer);

            var table = new Aspose.Pdf.Table
            {
                ColumnWidths       = "30 200 180 100",
                Border             = new Aspose.Pdf.BorderInfo(Aspose.Pdf.BorderSide.All, 0.5f, Aspose.Pdf.Color.LightGray),
                DefaultCellBorder  = new Aspose.Pdf.BorderInfo(Aspose.Pdf.BorderSide.All, 0.5f, Aspose.Pdf.Color.LightGray),
                DefaultCellPadding = new Aspose.Pdf.MarginInfo(6, 4, 6, 4)
            };

            var hdr = table.Rows.Add();
            hdr.BackgroundColor = Aspose.Pdf.Color.FromRgb(System.Drawing.Color.FromArgb(0xF2, 0xF2, 0xF2));
            AddHeaderCell(hdr, "#");
            AddHeaderCell(hdr, "Approved By");
            AddHeaderCell(hdr, "Department");
            AddHeaderCell(hdr, "Date");

            int idx = 1;
            foreach (var a in approvals)
            {
                var row = table.Rows.Add();
                AddCell(row, idx.ToString());
                AddCell(row, a.Name       ?? "");
                AddCell(row, a.Department ?? "");
                AddCell(row, a.Date       ?? "");
                idx++;
            }
            page.Paragraphs.Add(table);
        }

        private static void AddHeaderCell(Aspose.Pdf.Row row, string text)
        {
            var c = row.Cells.Add(text);
            foreach (var p in c.Paragraphs)
                if (p is Aspose.Pdf.Text.TextFragment tf)
                {
                    tf.TextState.FontSize  = 10;
                    tf.TextState.FontStyle = Aspose.Pdf.Text.FontStyles.Bold;
                }
        }

        private static void AddCell(Aspose.Pdf.Row row, string text)
        {
            var c = row.Cells.Add(text);
            foreach (var p in c.Paragraphs)
                if (p is Aspose.Pdf.Text.TextFragment tf)
                    tf.TextState.FontSize = 10;
        }

        // ---------------------------------------------------------------------
        // DOCX rewrite — find any prior "Approval History" section and replace
        // it with a fresh one at the end of the document. For Word, we CAN flow
        // content naturally because Aspose.Words owns the paragraph model.
        // ---------------------------------------------------------------------
        private static byte[] RewriteApprovalSectionDocx(byte[] docxBytes, List<Approval> approvals)
        {
            using (var inMs  = new MemoryStream(docxBytes))
            using (var outMs = new MemoryStream())
            {
                var doc = new Aspose.Words.Document(inMs);
                RemovePriorApprovalSectionDocx(doc);
                AppendApprovalSectionDocx(doc, approvals);
                doc.Save(outMs, Aspose.Words.SaveFormat.Docx);
                return outMs.ToArray();
            }
        }

        /// <summary>
        /// Finds the "Approval History" heading paragraph anywhere in the body and
        /// removes the heading plus every sibling node that follows it (typically
        /// the table we previously inserted). On the first run there's no heading
        /// to find — this is a no-op.
        /// </summary>
        private static void RemovePriorApprovalSectionDocx(Aspose.Words.Document doc)
        {
            foreach (Aspose.Words.Section section in doc.Sections)
            {
                var body = section.Body;
                Aspose.Words.Node markerNode = null;
                for (var n = body.FirstChild; n != null; n = n.NextSibling)
                {
                    if (n is Aspose.Words.Paragraph p
                        && string.Equals(p.GetText().Trim(), ApprovalPageMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        markerNode = n;
                        break;
                    }
                }
                if (markerNode == null) continue;

                int removed = 0;
                var cursor = markerNode;
                while (cursor != null)
                {
                    var next = cursor.NextSibling;
                    cursor.Remove();
                    cursor = next;
                    removed++;
                }
                LogInfo($"Removed {removed} prior approval node(s) from DOCX body.");
                return;
            }
        }

        private static void AppendApprovalSectionDocx(Aspose.Words.Document doc, List<Approval> approvals)
        {
            var builder = new Aspose.Words.DocumentBuilder(doc);
            builder.MoveToDocumentEnd();

            // Spacer + heading
            builder.ParagraphFormat.ClearFormatting();
            builder.Font.ClearFormatting();
            builder.Writeln();
            builder.Font.Bold = true;
            builder.Font.Size = 14;
            builder.Writeln(ApprovalPageMarker);

            // Table
            builder.Font.ClearFormatting();
            builder.Font.Bold = false;
            builder.Font.Size = 10;

            var table = builder.StartTable();

            // Header row
            builder.RowFormat.HeadingFormat = true;
            builder.CellFormat.Borders.LineStyle = Aspose.Words.LineStyle.Single;
            builder.CellFormat.Borders.Color = System.Drawing.Color.FromArgb(0xCC, 0xCC, 0xCC);
            builder.CellFormat.Shading.BackgroundPatternColor = System.Drawing.Color.FromArgb(0xF2, 0xF2, 0xF2);
            builder.Font.Bold = true;
            AddDocxCell(builder, "#",            Aspose.Words.Tables.PreferredWidth.FromPercent(6));
            AddDocxCell(builder, "Approved By",  Aspose.Words.Tables.PreferredWidth.FromPercent(40));
            AddDocxCell(builder, "Department",   Aspose.Words.Tables.PreferredWidth.FromPercent(34));
            AddDocxCell(builder, "Date",         Aspose.Words.Tables.PreferredWidth.FromPercent(20));
            builder.EndRow();

            // Data rows
            builder.RowFormat.HeadingFormat = false;
            builder.CellFormat.Shading.ClearFormatting();
            builder.Font.Bold = false;

            int idx = 1;
            foreach (var a in approvals)
            {
                AddDocxCell(builder, idx.ToString(),       Aspose.Words.Tables.PreferredWidth.FromPercent(6));
                AddDocxCell(builder, a.Name       ?? "",   Aspose.Words.Tables.PreferredWidth.FromPercent(40));
                AddDocxCell(builder, a.Department ?? "",   Aspose.Words.Tables.PreferredWidth.FromPercent(34));
                AddDocxCell(builder, a.Date       ?? "",   Aspose.Words.Tables.PreferredWidth.FromPercent(20));
                builder.EndRow();
                idx++;
            }
            builder.EndTable();
            table.PreferredWidth = Aspose.Words.Tables.PreferredWidth.FromPercent(100);
        }

        private static void AddDocxCell(Aspose.Words.DocumentBuilder b, string text, Aspose.Words.Tables.PreferredWidth width)
        {
            b.InsertCell();
            b.CellFormat.PreferredWidth = width;
            b.Write(text);
        }

        // ---------------------------------------------------------------------
        // Storage IO + IAM auth
        // ---------------------------------------------------------------------
        private sealed class TokenResponse { public string access_token { get; set; } }

        private async System.Threading.Tasks.Task<string> GetTokenAsync()
        {
            using (var client = new HttpClient { BaseAddress = new Uri(IamBaseUrl) })
            using (var req    = new HttpRequestMessage(HttpMethod.Post, "/connect/token"))
            {
                req.Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("grant_type",    "password"),
                    new KeyValuePair<string, string>("client_id",     AuthCaseClientId),
                    new KeyValuePair<string, string>("client_secret", AuthCaseClientSecret),
                    new KeyValuePair<string, string>("scope",         "IdentityServerApi"),
                    new KeyValuePair<string, string>("username",      AuthUserName),
                    new KeyValuePair<string, string>("password",      AuthUserPassword),
                });
                using (var resp = await client.SendAsync(req))
                {
                    resp.EnsureSuccessStatusCode();
                    return JsonConvert.DeserializeObject<TokenResponse>(
                        await resp.Content.ReadAsStringAsync()).access_token;
                }
            }
        }

        private async System.Threading.Tasks.Task<byte[]> DownloadAsync(string storageId, string token)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using (var resp = await client.GetAsync(
                    StorageBaseUrl + "Storage/Download?fileId=" + Uri.EscapeDataString(storageId)))
                {
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsByteArrayAsync();
                }
            }
        }

        private async System.Threading.Tasks.Task ReplaceAsync(string storageId, string fileName, string contentType, byte[] bytes, string token)
        {
            using (var client = new HttpClient())
            using (var form   = new MultipartFormDataContent())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                form.Add(new StringContent(fileName),                                "Name");
                form.Add(new StringContent((Path.GetExtension(fileName) ?? "").TrimStart('.')), "Extension");
                form.Add(new StringContent(contentType),                             "ContentType");
                form.Add(new StringContent(bytes.Length.ToString()),                 "FileSize");
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                form.Add(file, "Data", fileName);
                using (var resp = await client.PostAsync(
                    StorageBaseUrl + "Storage/ReplaceNoVersioning?fileId=" + Uri.EscapeDataString(storageId), form))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            $"ReplaceNoVersioning {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
                }
            }
        }

        // ---------------------------------------------------------------------
        // Aspose license — single application per process.
        // ---------------------------------------------------------------------
        private static void EnsureAsposeLicensed()
        {
            if (Interlocked.Exchange(ref _licenseApplied, 1) == 1) return;
            try
            {
                using (var s = new AsposeLicense().Get())
                    if (s != null) { s.Position = 0; new Aspose.Pdf.License().SetLicense(s); }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _licenseApplied, 0);
                LogException("Aspose license could not be applied — output will be evaluation-limited", ex);
            }
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------
        private static string GetProp(WorkflowItem i, string k)
        { try { var v = i?.Properties?[k]?.Value; return v == null ? "" : Convert.ToString(v); } catch { return ""; } }

        private static void LogInfo (string m) { Write("INFO ", m); }
        private static void LogWarn (string m) { Write("WARN ", m); }
        private static void LogError(string m) { Write("ERROR", m); }
        private static void LogException(string context, Exception ex)
        {
            var sb = new System.Text.StringBuilder().Append(context).Append(": ");
            for (var e = ex; e != null; e = e.InnerException)
                sb.Append('[').Append(e.GetType().FullName).Append("] ").Append(e.Message).Append(" --> ");
            Write("ERROR", sb.ToString());
        }
        private static void Write(string level, string message)
        {
            try
            {
                string path = Path.Combine(LogDirectory,
                    "BuildApprovalHistoryActivity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + level
                            + "  [tid:" + Thread.CurrentThread.ManagedThreadId + "]  " + message + Environment.NewLine;
                lock (LogLock)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(path, line, System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
