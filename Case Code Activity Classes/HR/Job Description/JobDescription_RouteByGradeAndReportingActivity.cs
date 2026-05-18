using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Globalization;

namespace Shared.Activities
{
    public class JobDescription_RouteByGradeAndReportingActivity : ActivityTemplate
    {
        #region Business rules 

        // Business rules (per the Job Description DOA matrix):
        //
        //   1. <b>Direct CEO reportees</b> — if the position reports directly to
        //      the CEO (form field <c>isDirectReporteeToCEO == true</c>), the JD is
        //      escalated straight to the CEO. The CHRO step is intentionally skipped
        //      because the CEO is the line manager and approving the JD themselves
        //      supersedes the CHRO review.
        //        -> nextApprovalRoute = "CEODirect"
        //
        //   2. <b>Grade A, B, C, or D (senior roles)</b> — JDs at these grades
        //      require both CHRO endorsement and CEO sign-off. CHRO reviews HR
        //      consistency (job family, banding, compensation alignment); CEO
        //      provides final approval.
        //        -> nextApprovalRoute = "CHROAndCEO"
        //
        //   3. <b>Grade E or F (operational roles)</b> — no further escalation
        //      beyond the Dept Head / AD HR step that has already completed.
        //        -> nextApprovalRoute = "Direct"
        //
        // Inputs (read from <see cref="WorkflowItem.Properties"/>):
        //   - <c>gradeLevel</c>            : string  "A" | "B" | "C" | "D" | "E" | "F"
        //   - <c>isDirectReporteeToCEO</c> : string  "true" | "false" (form checkbox)
        //
        // Output (written to <see cref="WorkflowItem.Properties"/>):
        //   - <c>nextApprovalRoute</c>     : "CEODirect" | "CHROAndCEO" | "Direct"
        //
        // The output is consumed by the outgoing transitions of the
        // "RouteByGradeAndReporting" gateway:
        //
        //   RouteByGradeAndReporting -> CEOApproval              when nextApprovalRoute == "CEODirect"
        //   RouteByGradeAndReporting -> CHROApproval -> CEO...   when nextApprovalRoute == "CHROAndCEO"
        //   RouteByGradeAndReporting -> StampApprovedDocuments   when nextApprovalRoute == "Direct"       
        #endregion


        private static readonly string[] SeniorGrades = { "A", "B", "C", "D" };

        public override void Execute(WorkflowItem workflowItem)
        {
            string grade = GetProp(workflowItem, "gradeLevel");
            bool isDirectReportee = ParseBool(GetProp(workflowItem, "isDirectReporteeToCEO"));

            string nextApprovalRoute;
            if (isDirectReportee)
            {
                // CEO direct reportee — straight to CEO, skip CHRO.
                nextApprovalRoute = "CEODirect";
            }
            else if (IsSenior(grade))
            {
                // Grade A-D — CHRO then CEO.
                nextApprovalRoute = "CHROAndCEO";
            }
            else
            {
                // Grade E / F — Dept Head approval is final.
                nextApprovalRoute = "Direct";
            }

            SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);
        }

        public override void Complete(WorkflowItem workflowItem) { }

        private static bool IsSenior(string g)
        {
            if (string.IsNullOrWhiteSpace(g)) return false;
            string up = g.Trim().ToUpperInvariant();
            foreach (var s in SeniorGrades) if (s == up) return true;
            return false;
        }

        private static bool ParseBool(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string v = s.Trim().ToLowerInvariant();
            return v == "true" || v == "yes" || v == "1" || v == "on";
        }

        private static string GetProp(WorkflowItem i, string k)
        {
            try { var v = i?.Properties?[k]?.Value; return v == null ? "" : Convert.ToString(v); } catch { return ""; }
        }

        private static void SetProp(WorkflowItem i, string k, object v)
        { if (i?.Properties == null) return; try { var p = i.Properties[k]; if (p != null) p.Value = v; } catch { } }
    }
}