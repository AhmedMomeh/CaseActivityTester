using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Globalization;

namespace Shared.Activities
{
    public class ShortlistedCandidateList_RouteByPositionAndGradeAActivity : ActivityTemplate
    {
        #region Business rules 

        /// <summary>
        /// Routes the Candidate Selection Form after CPCO approval.      
        ///
        ///   1. Exception positions go to the Board of Directors regardless of grade:
        ///        - CEO
        ///        - VP - Internal Audit
        ///        - Board Office Manager
        ///        - N-1 Leadership
        ///        -> nextApprovalRoute = "BoDApproval"
        ///
        ///   2. Grade A, B, C, or D (non-exception positions) require CEO approval:
        ///        -> nextApprovalRoute = "CEOApproval"
        ///
        ///   3. Grade E or F (non-exception positions) are final after CPCO:
        ///        -> nextApprovalRoute = "Direct"
        ///
        /// Inputs  (form fields persisted as WorkflowItem.Properties):
        ///   - positionCategory : "Standard" | "CEO" | "VPInternalAudit" |
        ///                        "BoardOfficeManager" | "N1Leadership"
        ///   - gradeLevel       : "A" | "B" | "C" | "D" | "E" | "F"
        ///
        /// Output (written back to WorkflowItem.Properties):
        ///   - nextApprovalRoute : "BoDApproval" | "CEOApproval" | "Direct"
        /// </summary>       
        #endregion


        private static readonly string[] SeniorGrades = { "A", "B", "C", "D" };
        private static readonly string[] ExceptionPositions =
        {
              "CEO", "VPInternalAudit", "BoardOfficeManager", "N1Leadership"
        };

        public override void Execute(WorkflowItem workflowItem)
        {
            string position = GetProp(workflowItem, "positionCategory");
            string grade = GetProp(workflowItem, "gradeLevel");

            string nextApprovalRoute;
            if (IsException(position))
            {
                // Rule 1: any of the four protected roles -> Board of Directors approval.
                nextApprovalRoute = "BoDApproval";
            }
            else if (IsSenior(grade))
            {
                // Rule 2: Grade A-D non-exception -> CEO approval.
                nextApprovalRoute = "CEOApproval";
            }
            else
            {
                // Rule 3: Grade E/F (or unknown) non-exception -> CPCO was final.
                nextApprovalRoute = "Direct";
            }

            SetProp(workflowItem, "nextApprovalRoute", nextApprovalRoute);
        }

        public override void Complete(WorkflowItem workflowItem) { }

        private static bool IsException(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return false;
            string norm = p.Trim();
            foreach (var x in ExceptionPositions)
                if (string.Equals(x, norm, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsSenior(string g)
        {
            if (string.IsNullOrWhiteSpace(g)) return false;
            string up = g.Trim().ToUpperInvariant();
            foreach (var s in SeniorGrades) if (s == up) return true;
            return false;
        }

        private static string GetProp(WorkflowItem i, string k)
        {
            try { var v = i?.Properties?[k]?.Value; return v == null ? "" : Convert.ToString(v); } catch { return ""; }
        }

        private static void SetProp(WorkflowItem i, string k, object v)
        { if (i?.Properties == null) return; try { var p = i.Properties[k]; if (p != null) p.Value = v; } catch { } }
    }
}