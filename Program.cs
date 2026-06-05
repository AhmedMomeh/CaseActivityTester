using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using Microsoft.Data.SqlClient;
using Shared.Activities;
using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace ActivityTester
{
    internal static class PortalResolver
    {
        // The Portal install is still probed AS A FALLBACK (useful on a developer
        // machine that has the full Portal installed). The PRIMARY source is now
        // the local "lib/" + "runtimes/" folders copied next to the executable —
        // those are bundled into git so the project runs on machines where the
        // Portal isn't installed.
        public const string PortalDir = @"C:\Program Files\Intalio\UC_CasePortal";

        // AppContext.BaseDirectory = the folder containing the .exe (bin\Debug\net8.0\
        // when developing, or wherever the published binaries land in production).
        private static readonly string AppDir    = AppContext.BaseDirectory;
        private static readonly string LocalLib  = AppDir;                            // lib\*.dll get copied flat into bin
        private static readonly string LocalRids = Path.Combine(AppDir, "runtimes");  // lib\runtimes\... preserved by csproj
        private static readonly string NativeDirAppLocal = Path.Combine(LocalRids, Rid(), "native");
        private static readonly string NativeDirPortal   = Path.Combine(PortalDir, "runtimes", Rid(), "native");

        // Managed-assembly probe order: local-lib paths win first (so a developer
        // machine still uses the bundled DLLs by default), Portal install is the
        // fallback (handy when newer Portal DLLs are available locally). Within
        // each location, RID-specific paths come FIRST because for some packages
        // (e.g. Microsoft.Data.SqlClient) the flat-folder DLL is a stub and the
        // real implementation lives under runtimes/<rid>/lib/<tfm>/. Probe order
        // within each base: rid-specific → os-family → unix → flat. TFM tried
        // newest → oldest (net8 → net6).
        private static readonly string[] ProbeDirs = BuildProbeDirs();

        // Runs before any other code in this assembly, including Main. Registering the
        // probe here ensures Intalio types referenced by Main's signature can be loaded.
        [ModuleInitializer]
        internal static void Init()
        {
            AssemblyLoadContext.Default.Resolving += (ctx, asmName) =>
            {
                foreach (var dir in ProbeDirs)
                {
                    string candidate = Path.Combine(dir, asmName.Name + ".dll");
                    if (File.Exists(candidate))
                        return ctx.LoadFromAssemblyPath(candidate);
                }
                return null;
            };

            // Native deps (Microsoft.Data.SqlClient.SNI, libSkiaSharp, harfbuzz, etc.)
            // come from the local lib first, fall back to Portal install.
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, libraryName) =>
            {
                foreach (var dir in new[] { NativeDirAppLocal, NativeDirPortal })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var name in new[] { libraryName, libraryName + ".dll" })
                    {
                        string p = Path.Combine(dir, name);
                        if (File.Exists(p) && NativeLibrary.TryLoad(p, out var handle))
                            return handle;
                    }
                }
                return IntPtr.Zero;
            };
        }

        private static string[] BuildProbeDirs()
        {
            string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ? "linux" :
                        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "osx" : "unix";
            string rid = Rid();
            var dirs = new System.Collections.Generic.List<string>();

            // ----- Local app-folder probes (preferred) -----
            foreach (var family in new[] { rid, os, "unix" })
            foreach (var tfm    in new[] { "net8.0", "net7.0", "net6.0" })
                dirs.Add(Path.Combine(LocalRids, family, "lib", tfm));
            dirs.Add(LocalLib);                       // flat copies in bin

            // ----- Portal-install probes (fallback) -----
            foreach (var family in new[] { rid, os, "unix" })
            foreach (var tfm    in new[] { "net8.0", "net7.0", "net6.0" })
                dirs.Add(Path.Combine(PortalDir, "runtimes", family, "lib", tfm));
            dirs.Add(PortalDir);

            return dirs.ToArray();
        }

        private static string Rid()
        {
            string os =
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ? "linux" :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "osx" : "unknown";
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64   => "x64",
                Architecture.X86   => "x86",
                Architecture.Arm64 => "arm64",
                Architecture.Arm   => "arm",
                _                  => "x64",
            };
            return $"{os}-{arch}";
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            // ----------------------------------------------------------------
            // Run-mode switch.
            //   ActivityTester.exe              → original activity-testing mode (below)
            //   ActivityTester.exe mock-api     → starts the mock JDE Orchestrator HTTP server
            // The mock-api branch never touches the activity setup (no DB conn,
            // no Portal config) so it's safe to run on machines that don't have
            // the Portal data layer reachable.
            // ----------------------------------------------------------------
            if (args.Length > 0 &&
                (args[0].Equals("mock-api",   System.StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("--mock-api", System.StringComparison.OrdinalIgnoreCase) ||
                 args[0].Equals("jde-mock",   System.StringComparison.OrdinalIgnoreCase)))
            {
                var rest = new string[args.Length - 1];
                System.Array.Copy(args, 1, rest, 0, rest.Length);
                return ActivityTester.JdeMock.JdeMockServer.Run(rest);
            }

            // Work from the app's own directory so the data layer finds the bundled
            // appsettings.json + license + native deps. If the bin folder doesn't
            // have an appsettings.json (older build) fall back to the Portal dir
            // when it exists, otherwise keep the current working directory.
            string preferredDir = AppContext.BaseDirectory;
            if (!File.Exists(Path.Combine(preferredDir, "appsettings.json")) && Directory.Exists(PortalResolver.PortalDir))
                preferredDir = PortalResolver.PortalDir;
            Directory.SetCurrentDirectory(preferredDir);

            // The Portal's Startup populates static Configuration props from appsettings.json.
            // We replicate the minimum the activity needs. (Calling ConfigureSystem() would
            // also run EF migrations against the live DB, which we don't want from a debug tool.)
            // NOTE: there are TWO independent Configuration classes that each own a separate
            // DbConnectionString static, used by two different DbContexts pointing at the
            // same DB:
            //   * Intalio.Case.Core.Configuration  -> Case.Core DAL (Document, Attachment, …)
            //   * Intalio.Core.Configuration       -> Core DAL      (NotificationTemplate, User, …)
            // Set both, otherwise the second DbContext throws "ConnectionString property has
            // not been initialized" the moment something like NotificationTemplate.FindByName
            // tries to open a connection.
            const string DbConnString =
                "Server=.;Database=UC_Case;MultipleActiveResultSets=true;Integrated Security=True;TrustServerCertificate=true;";
            Intalio.Case.Core.Configuration.DbConnectionString = DbConnString;
            Intalio.Case.Portal.Core.Configuration.StorageServerUrl = "http://localhost:44444/";

            // Intalio.Core's own Configuration class lives in a different namespace and
            // doesn't expose DbConnectionString at the top level — the property is on a
            // nested/sibling class whose exact path differs between minor versions. Use
            // reflection to find every static settable string property named
            // "*ConnectionString" inside the Intalio.Core assembly and set it. Cheap,
            // version-agnostic, and self-documenting (logs which ones it sets).
            SetIntalioCoreConnectionStrings(DbConnString);

            // ===== TEMP DIAGNOSTIC: where are the attachments for this document? =====
            // Flip to true to dump every Attachment lookup method + raw SQL for one doc.
            const bool runAttachmentDiagnostic = false;
            if (runAttachmentDiagnostic)
            {
                DiagnoseAttachments(documentId: 19);
                return 0;
            }
            // =========================================================================

            // ===== TEMP DIAGNOSTIC: dump ManageAttachment methods so we can pick the
            // right Get-bytes / Replace overload without guessing.  Flip to true once,
            // copy the printed signatures into the activity, then flip back to false.
            const bool dumpManageAttachment = false;
            if (dumpManageAttachment)
            {
                // Resolve the actual generic-argument return type of GetAttachmentData.
                DumpReturnTaskType("Intalio.Case.Portal.Core.API.ManageAttachment", "GetAttachmentData");
                // FileViewModel: the bytes carrier for Replace().
                FindType("FileViewModel");
                return 0;
            }

            const bool dumpStorageHelpers = false;
            if (dumpStorageHelpers)
            {
                DumpType("Intalio.Core.Helper");
                DumpType("Intalio.Case.Portal.Core.Configuration");
                DumpType("Intalio.Case.Portal.Core.DAL.Document", grep: "Find|Created|User|Structure|Group");
                return 0;
            }

            const bool dumpUserPerms = false;
            if (dumpUserPerms)
            {
                DumpType("Intalio.Case.Portal.Core.DAL.Users", grep: "Structure|Group|Find|Id");
                FindType("UserStructure");
                FindType("UserGroup");
                return 0;
            }

            const bool dumpAttachmentModel = false;
            const bool dumpAsposeLicense = false;
            const bool dumpWorkflowItem = false;
            const bool dumpTaskAndUser = false;
            const bool dumpUsersFull = false;
            if (dumpUsersFull)
            {
                DumpType("Intalio.Case.Portal.Core.DAL.Users");
                return 0;
            }
            if (dumpAsposeLicense)
            {
                DumpType("Intalio.Core.Utility.AsposeLicense");
                return 0;
            }
            if (dumpAttachmentModel)
            {
                try { System.Reflection.Assembly.LoadFile(@"C:\Program Files\Intalio\UC_StorageFileSystem\Intalio.Storage.FileSystem.Core.dll"); } catch (Exception e) { Console.WriteLine("load AttachmentModel asm: " + e.Message); }
                try { System.Reflection.Assembly.LoadFile(@"C:\Program Files\Intalio\UC_StorageFileSystem\Intalio.Storage.Interface.dll"); } catch (Exception e) { Console.WriteLine("load Interface asm: " + e.Message); }
                FindType("AttachmentModel");
                return 0;
            }

            const bool dumpStructureGroup = false;
            if (dumpStructureGroup)
            {
                FindType("Structure");
                FindType("Group");
                DumpType("Intalio.Case.Portal.Core.DAL.Structure", grep: "ListBy|FindBy|UserId|Ids");
                DumpType("Intalio.Case.Portal.Core.DAL.Group",     grep: "ListBy|FindBy|UserId|Ids");
                return 0;
            }

            // ===== Probe for Structure / User-Structure mapping =====
            const bool dumpStructureLookup = false;
            const bool dumpIdentityHelper = false;
            const bool dumpUserModel = false;
            const bool dumpCoreConfig = false;
            const bool dumpIamConfig = false;
            const bool dumpClientCredentials = false;
            const bool dumpIdentityHelperFields = false;
            const bool dumpDocumentFull = false;
            const bool dumpSmtp = false;
            const bool dumpEmailApi = false;
            const bool dumpNotifApi = false;
            const bool dumpEmailType = false;
            if (dumpEmailType)
            {
                DumpType("Intalio.Core.Email");
                try
                {
                    Console.WriteLine("\n--- Live SmtpSettings ---");
                    var s = Intalio.Core.Configuration.SmtpSettings;
                    Console.WriteLine(s == null ? "(null - not loaded)" : "loaded");
                    if (s != null) foreach (var p in s.GetType().GetProperties())
                        Console.WriteLine($"  {p.Name} = {p.GetValue(s)}");
                } catch (Exception e) { Console.WriteLine("Read failed: " + e.Message); }
                return 0;
            }
            if (dumpNotifApi)
            {
                DumpType("Intalio.Core.API.ManageNotificationTemplate");
                DumpType("Intalio.Core.DAL.NotificationTemplate", grep: "Find|List|Get");
                FindType("SmtpHelper");
                FindType("EmailService");
                FindType("Email");
                return 0;
            }
            if (dumpEmailApi)
            {
                FindType("NotificationTemplate");
                FindType("ManageNotificationTemplate");
                FindType("EmailNotification");
                FindType("EmailHelper");
                FindType("SendEmailActivity");
                return 0;
            }
            if (dumpDocumentFull)
            {
                DumpType("Intalio.Case.Portal.Core.DAL.Document");
                return 0;
            }
            const bool dumpAttachmentList = false;
            if (dumpAttachmentList)
            {
                DumpType("Intalio.Case.Portal.Core.DAL.Attachment", grep: "ListByDocumentId|ListOriginal|FindByDocumentId|FindNonAdditional|Async");
                return 0;
            }
            if (dumpIdentityHelperFields)
            {
                var t = Type.GetType("Intalio.Core.API.IdentityHelper, Intalio.Core");
                if (t != null) {
                    Console.WriteLine("=== IdentityHelper fields (public + non-public, static + instance) ===");
                    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                        Console.WriteLine($"  {(f.IsStatic ? "static " : "")}{f.FieldType.Name} {f.Name} {(f.IsStatic ? " = " + (f.GetValue(null) ?? "null") : "")}");
                    Console.WriteLine("=== IdentityHelper properties ===");
                    foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                        Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
                }
                // Also dump Helper class which has HttpGet/HttpPost - may carry the URL
                var helper = Type.GetType("Intalio.Core.Helper, Intalio.Core");
                if (helper != null) {
                    Console.WriteLine("=== Intalio.Core.Helper static fields ===");
                    foreach (var f in helper.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
                    {
                        object v = null;
                        try { v = f.GetValue(null); } catch { }
                        Console.WriteLine($"  {f.FieldType.Name} {f.Name} = {v ?? "null"}");
                    }
                }
                return 0;
            }
            if (dumpClientCredentials)
            {
                DumpType("Intalio.Core.ClientCredentialAccessToken");
                FindType("IdentityModel");
                FindType("ClientCredentials");
                FindType("IamSettings");
                // Probe fields too (not just properties)
                var t = Type.GetType("Intalio.Core.ClientCredentialAccessToken, Intalio.Core");
                if (t != null) {
                    Console.WriteLine("\n=== ClientCredentialAccessToken fields ===");
                    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                        Console.WriteLine($"  {(f.IsStatic ? "static " : "")}{f.FieldType.Name} {f.Name}");
                }
                return 0;
            }
            if (dumpIamConfig)
            {
                FindType("IdentityServerSettings");
                FindType("IdentityServer");
                FindType("IdentityConfiguration");
                FindType("ClientCredentialAccessToken");
                // Look for any Core type with URL fields
                try { System.Reflection.Assembly.Load("Intalio.Core"); } catch { }
                Console.WriteLine("\n===== Properties on Intalio.* types containing 'Identity' or 'BaseUrl' =====");
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = a.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.FullName == null || !t.FullName.StartsWith("Intalio.")) continue;
                        foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                        {
                            if (p.Name.IndexOf("IdentityServer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("IamUrl",         StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.Name.IndexOf("BaseUrl",        StringComparison.OrdinalIgnoreCase) >= 0)
                                Console.WriteLine($"  {t.FullName}.{p.Name} ({p.PropertyType.Name})");
                        }
                    }
                }
                return 0;
            }
            if (dumpCoreConfig)
            {
                FindType("Configuration");
                DumpType("Intalio.Core.Configuration");
                return 0;
            }
            if (dumpUserModel)
            {
                DumpProps("Intalio.Core.Model.UserModel");
                FindType("UserModel");
                return 0;
            }
            if (dumpStructureLookup)
            {
                // Force-load the Portal asm so all types are visible.
                try { System.Reflection.Assembly.Load("Intalio.Case.Portal.Core"); } catch { }
                try { System.Reflection.Assembly.Load("Intalio.Case.Core"); } catch { }

                Console.WriteLine("\n===== Any DAL type with 'Structure' in its full name =====");
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    System.Type[] types;
                    try { types = a.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.FullName == null) continue;
                        if (!t.FullName.StartsWith("Intalio.")) continue;
                        if (t.FullName.IndexOf("Structure", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Console.WriteLine("  " + t.FullName);
                    }
                }

                Console.WriteLine("\n===== Methods/props returning a 'Structure'-like type =====");
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    System.Type[] types;
                    try { types = a.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.FullName == null || !t.FullName.StartsWith("Intalio.")) continue;
                        try
                        {
                            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                            {
                                var rn = m.ReturnType?.Name ?? "";
                                if (rn.IndexOf("Structure", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                                if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                                Console.WriteLine($"  {t.FullName}.{m.Name}() -> {rn}");
                            }
                        }
                        catch { }
                    }
                }
                return 0;
            }
            // =========================================================================

            // ===================================================================
            //                EDIT THE TEST CASE BELOW
            // ===================================================================
            // 1) Pick the activity to run (uncomment one).
            // 2) Set the workflow item properties you want to feed in.
            // 3) Press F5 in Visual Studio (or run Run.cmd from this folder).
            // ===================================================================

            //ActivityTemplate activity = new ArchiveHRDocumentsToDMSActivity();
            //ActivityTemplate activity = new ArchiveResumeActivity();
            //ActivityTemplate activity = new ChangeStatusToClosedActivity();
            //ActivityTemplate activity = new HRRouteContractByGradeActivity();
            //ActivityTemplate activity = new NextApprovalRoleActivity();
            //ActivityTemplate activity = new IPO_IssuanceOfPurchaseOrder_RouteByAmountActivity();
            //ActivityTemplate activity = new StampApprovedDocumentsActivity();
            //ActivityTemplate activity = new BuildApprovalHistoryActivity();
            //ActivityTemplate activity = new BuildApprovalHistoryActivity();
            //SendCaseDocumentsEmailActivity activity = new SendCaseDocumentsEmailActivity();
            // RouteToCreatorStructureManagerActivity activity = new RouteToCreatorStructureManagerActivity();

            // RouteToCreatorLineManagerActivity activity = new RouteToCreatorLineManagerActivity();

            SetCurrentTaskNameActivity activity = new SetCurrentTaskNameActivity();
            
            var props = new PropertyCollection
            {
                // For BuildApprovalHistoryActivity:
                new Property { Name = "DocumentId",       Value = "140" },
                new Property { Name = "approvalHistory",  Value = "89" },

                // For StampApprovedDocumentsActivity:
                //new Property { Name = "DocumentId",    Value = "89" },
                //new Property { Name = "requesterName", Value = "Ahmed Momeh" },

                // For ArchiveEmployeeDocumentActivity:
                //new Property { Name = "DocumentId",       Value = "89" },
                //new Property { Name = "EmployeeId",       Value = "3"   },
                //new Property { Name = "DocumentCategory", Value = "Letters" },

                // For HRRouteContractByGradeActivity use these instead:
                //new Property { Name = "grade",            Value = "A" },
                //new Property { Name = "nextApprovalRole", Value = ""  },
            };

            // ===================================================================

            var item = new WorkflowItem { Properties = props };

            Console.WriteLine($"==> Running {activity.GetType().FullName}");
            foreach (var p in props) Console.WriteLine($"    {p.Name} = {p.Value}");

            try
            {
                activity.Execute(item);
                Console.WriteLine("==> Execute completed.");

                activity.Complete(item);
                Console.WriteLine("==> Complete completed.");

                Console.WriteLine("Final properties after run:");
                foreach (var p in item.Properties) Console.WriteLine($"    {p.Name} = {p.Value}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("==> FAILED");
                DumpException(ex);
                return 1;
            }
        }

        // Sets every static-settable string property named "*ConnectionString" found
        // on types declared in the Intalio.Core assembly. Both the property name and the
        // declaring type's namespace vary across Intalio versions (Intalio.Core.Configuration,
        // Intalio.Core.DatabaseConfiguration, Intalio.Core.DAL.Configuration, etc.), so we
        // discover them dynamically and log every hit, making mismatches obvious.
        private static void SetIntalioCoreConnectionStrings(string value)
        {
            try
            {
                var asm = System.Reflection.Assembly.Load("Intalio.Core");
                int hits = 0;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex)
                { types = ex.Types.Where(t => t != null).ToArray(); }

                foreach (var t in types)
                {
                    if (t == null || t.IsGenericTypeDefinition) continue;
                    foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public
                                                   | System.Reflection.BindingFlags.NonPublic
                                                   | System.Reflection.BindingFlags.Static))
                    {
                        if (!p.CanWrite) continue;
                        if (p.PropertyType != typeof(string)) continue;
                        if (!p.Name.EndsWith("ConnectionString", StringComparison.Ordinal)) continue;
                        try
                        {
                            p.SetValue(null, value);
                            Console.WriteLine($"  set {t.FullName}.{p.Name}");
                            hits++;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  FAILED {t.FullName}.{p.Name}: {ex.Message}");
                        }
                    }
                }
                if (hits == 0)
                    Console.WriteLine("  WARN: no *ConnectionString static property found in Intalio.Core");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WARN: couldn't reflect Intalio.Core for connection strings: {ex.Message}");
            }
        }

        private static void DiagnoseAttachments(long documentId)
        {
            Console.WriteLine($"\n===== Attachment diagnostic for DocumentId = {documentId} =====\n");

            // 1) Each DAL lookup method, with its specific filter.
            try
            {
                var a = new Attachment();
                DumpList("Attachment.FindByDocumentId (excludes original)",        a.FindByDocumentId(documentId));
                DumpList("Attachment.FindNonAdditionalByDocumentId (only original)", a.FindNonAdditionalByDocumentId(documentId));
                DumpList("Attachment.ListOriginalDocumentByDocumentId (original+additional)", a.ListOriginalDocumentByDocumentId(documentId));
            }
            catch (Exception ex) { Console.Error.WriteLine("DAL probe failed: " + ex.Message); }

            // 2) Raw SQL: count every row in Attachment for this document and any task
            //    that belongs to it. This is the ground truth — no filters applied.
            try
            {
                using var conn = new SqlConnection(Intalio.Case.Core.Configuration.DbConnectionString);
                conn.Open();

                Console.WriteLine("\n--- raw Attachment rows where DocumentId = @id ---");
                DumpSql(conn,
                    @"SELECT Id, DocumentId, TaskId, Name, [Type], IsAdditional, Status, IsLocked, Size
                      FROM Attachment WHERE DocumentId = @id",
                    documentId);

                Console.WriteLine("\n--- raw Attachment rows linked via Task that belongs to this Document ---");
                DumpSql(conn,
                    @"SELECT a.Id, a.DocumentId, a.TaskId, a.Name, a.[Type], a.IsAdditional, a.Status, a.IsLocked, a.Size
                      FROM Attachment a
                      INNER JOIN Task t ON t.Id = a.TaskId
                      WHERE t.DocumentId = @id",
                    documentId);

                Console.WriteLine("\n--- DocumentTypes / form-attachment data for this Document ---");
                DumpSql(conn,
                    @"SELECT dt.Id AS DocumentTypeId, dt.Name, dt.DocumentTypesAttachmentDataId,
                              CASE WHEN ad.Data IS NULL THEN 0 ELSE DATALENGTH(ad.Data) END AS BytesLen
                       FROM DocumentTypes dt
                       LEFT JOIN DocumentTypesAttachmentData ad ON ad.Id = dt.DocumentTypesAttachmentDataId
                       WHERE dt.DocumentTypeBaseId IN (SELECT DocumentTypeBaseId FROM Document WHERE Id = @id)",
                    documentId);
            }
            catch (Exception ex) { Console.Error.WriteLine("\nSQL probe failed: " + ex.Message); }
        }

        private static void DumpList(string label, System.Collections.Generic.IEnumerable<Attachment> items)
        {
            int n = 0;
            Console.WriteLine($"\n[{label}]");
            foreach (var a in items)
            {
                Console.WriteLine($"  id={a.Id}  name={a.Name}  type={a.Type}  isAdditional={a.IsAdditional}  status={a.Status}  taskId={a.TaskId}  docId={a.DocumentId}");
                n++;
            }
            if (n == 0) Console.WriteLine("  (none)");
            Console.WriteLine($"  -> count: {n}");
        }

        private static void DumpSql(SqlConnection conn, string sql, long id)
        {
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            try
            {
                using var rdr = cmd.ExecuteReader();
                int n = 0;
                while (rdr.Read())
                {
                    if (n == 0)
                    {
                        for (int i = 0; i < rdr.FieldCount; i++) Console.Write($"{rdr.GetName(i),-22}");
                        Console.WriteLine();
                    }
                    for (int i = 0; i < rdr.FieldCount; i++)
                    {
                        var v = rdr.IsDBNull(i) ? "NULL" : rdr.GetValue(i).ToString();
                        Console.Write($"{v,-22}");
                    }
                    Console.WriteLine();
                    n++;
                }
                Console.WriteLine($"  -> rows: {n}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("  query error: " + ex.Message);
            }
        }

        private static void FindStringConstants(string substring)
        {
            Console.WriteLine($"\n===== Const strings containing: {substring} =====");
            try { System.Reflection.Assembly.Load("Intalio.Case.Portal.Core"); } catch { }
            try { System.Reflection.Assembly.Load("Intalio.Case.Core"); } catch { }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t.FullName == null) continue;
                    if (!t.FullName.StartsWith("Intalio.")) continue;
                    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
                    {
                        if (!f.IsLiteral || f.IsInitOnly) continue;
                        if (f.FieldType != typeof(string)) continue;
                        try
                        {
                            string v = (string)f.GetRawConstantValue();
                            if (v != null && v.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                                Console.WriteLine($"  {t.FullName}.{f.Name} = \"{v}\"");
                        } catch { }
                    }
                }
            }
        }

        private static void FindType(string simpleName)
        {
            Console.WriteLine($"\n===== Searching for type by simple name: {simpleName} =====");
            try { System.Reflection.Assembly.Load("Intalio.Case.Portal.Core"); } catch { }
            try { System.Reflection.Assembly.Load("Intalio.Case.Core"); } catch { }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t.Name == simpleName)
                    {
                        Console.WriteLine($"  FOUND: {t.FullName}");
                        foreach (var p in t.GetProperties().OrderBy(p => p.Name))
                            Console.WriteLine($"    {p.PropertyType.Name}  {p.Name}");
                    }
                }
            }
        }

        private static Type ResolveType(string fullName)
        {
            try { System.Reflection.Assembly.Load("Intalio.Case.Portal.Core"); } catch { }
            try { System.Reflection.Assembly.Load("Intalio.Case.Core"); } catch { }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var x = a.GetType(fullName, false); if (x != null) return x; } catch { }
            }
            // last resort: search by simple name
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { foreach (var x in a.GetTypes()) if (x.FullName == fullName) return x; } catch { }
            }
            return null;
        }

        private static void DumpReturnTaskType(string typeName, string methodName)
        {
            var t = ResolveType(typeName);
            if (t == null) { Console.WriteLine($"({typeName} not found)"); return; }
            foreach (var m in t.GetMethods()) if (m.Name == methodName)
            {
                var ret = m.ReturnType;
                Type inner = ret.IsGenericType ? ret.GetGenericArguments()[0] : ret;
                Console.WriteLine($"\n[{typeName}.{methodName}] return: {ret.FullName} -> inner: {inner.FullName}");
                DumpProps(inner.FullName);
            }
        }

        private static void DumpProps(string fullName)
        {
            Console.WriteLine($"\n===== {fullName} (props) =====");
            var t = ResolveType(fullName);
            if (t == null) { Console.WriteLine("  (not found)"); return; }
            foreach (var p in t.GetProperties().OrderBy(p => p.Name))
                Console.WriteLine($"  {p.PropertyType.Name}  {p.Name}");
        }

        private static void DumpType(string fullName, string grep = null)
        {
            Console.WriteLine($"\n===== {fullName} =====");
            Type t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType(fullName, false); if (t != null) break; } catch { }
            }
            if (t == null)
            {
                // Force-load Portal assemblies if not yet referenced
                try { System.Reflection.Assembly.Load("Intalio.Case.Portal.Core"); } catch { }
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = a.GetType(fullName, false); if (t != null) break; } catch { }
                }
            }
            if (t == null) { Console.WriteLine("  (type not found)"); return; }

            var re = grep == null ? null : new System.Text.RegularExpressions.Regex(grep);
            var flags = System.Reflection.BindingFlags.Public
                      | System.Reflection.BindingFlags.Instance
                      | System.Reflection.BindingFlags.Static
                      | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMethods(flags).OrderBy(m => m.Name))
            {
                if (re != null && !re.IsMatch(m.Name)) continue;
                var ps = string.Join(", ", m.GetParameters()
                          .Select(p => p.ParameterType.Name + " " + p.Name));
                Console.WriteLine($"  {(m.IsStatic ? "static " : "")}{m.ReturnType.Name}  {m.Name}({ps})");
            }
        }

        private static void DumpException(Exception ex)
        {
            int depth = 0;
            for (var e = ex; e != null; e = e.InnerException, depth++)
            {
                Console.Error.WriteLine(new string('-', 60));
                Console.Error.WriteLine($"[{depth}] {e.GetType().FullName}: {e.Message}");
                Console.Error.WriteLine(e.StackTrace);
                if (e is AggregateException agg)
                {
                    foreach (var inner in agg.InnerExceptions) DumpException(inner);
                    break;
                }
            }
        }
    }
}
