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
    public class NextApprovalRoleActivity : ActivityTemplate
    {
        // Per Delegation of Authority:
        //   Non-Budgeted, Grade D and above  -> CEO      
        //   Exception positions              -> Board of Directors
        //   Budgeted standard                -> Standard (no extra approval)
        private static readonly string[] CeoGrades = { "A", "B", "C", "D" };      
        private static readonly string[] ExceptionKeywords =
        {
              "ceo", "vp internal audit", "board office manager", "N-1 Leadership"   // got to ceo then bord  it ceo go to board
        };

        public override void Execute(WorkflowItem workflowItem)
        {
            string gradeLevel = GetProp(workflowItem, "gradeLevel");
            string budget = GetProp(workflowItem, "budgetStatus");
            string position = GetProp(workflowItem, "requiredPosition").ToLowerInvariant();

            string nextApprovalRole;

            if (ExceptionKeywords.Any(k => position.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                nextApprovalRole = "BoardApproval";
            }
            else if (string.Equals(budget, "nonBudgeted", StringComparison.OrdinalIgnoreCase))
            {
                if (CeoGrades.Contains(gradeLevel, StringComparer.OrdinalIgnoreCase))
                    nextApprovalRole = "CEOApproval";            
                else
                    nextApprovalRole = "Standard"; // budgeted -> no extra approval
            }
            else
            {
                nextApprovalRole = "Standard"; // budgeted -> no extra approval
            }

            SetProp(workflowItem, "nextApprovalRole", nextApprovalRole);
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
