using Intalio.Case.Core.Objects;
using Intalio.Case.Core.Templates;
using Intalio.Case.Portal.Core.DAL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shared.Activities
{
    public class HRRouteContractByGradeActivity : ActivityTemplate
    {
        // Grade D and above (D, C, B, A) -> CPCO extended approval
        // Grade E and below (E, F)       -> straight to Archive In Employee File
        private static readonly string[] CpcoGrades = { "A", "B", "C", "D" };

        public override void Execute(WorkflowItem workflowItem)
        {
            string grade = GetProp(workflowItem, "grade");
            string next = CpcoGrades.Contains(grade, StringComparer.OrdinalIgnoreCase)
                ? "CPCOApproval"
                : "ArchiveInEmployeeFile";
            SetProp(workflowItem, "nextApprovalRole", next);
        }

        public override void Complete(WorkflowItem workflowItem)
        {
            // No-op: this activity is fully automated.
        }

        // ---------- helpers ----------
        private static string GetProp(WorkflowItem item, string key)
        {
            try
            {
                var val = item?.Properties?[key]?.Value;
                return val == null ? string.Empty : Convert.ToString(val);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void SetProp(WorkflowItem item, string key, object value)
        {
            if (item == null || item.Properties == null) return;
            try
            {
                var prop = item.Properties[key];
                if (prop != null) prop.Value = value;
            }
            catch
            {
                // property not declared on this workflow — silently skip
            }
        }
    }
}
