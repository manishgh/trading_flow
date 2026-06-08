using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TradingFlow.Domain.Audit;

namespace TradingFlow.Web.Pages
{
    public class AuditModel : PageModel
    {
        private readonly IDecisionAuditRepository _auditRepo;

        public AuditModel(IDecisionAuditRepository auditRepo)
        {
            _auditRepo = auditRepo;
        }

        public string RunName { get; set; } = string.Empty;
        public IReadOnlyList<DecisionAuditRecord> Records { get; set; } = Array.Empty<DecisionAuditRecord>();

        public async Task<IActionResult> OnGetAsync(string runName)
        {
            RunName = runName;
            Records = await _auditRepo.GetAuditsByRunNameAsync(runName, default);
            return Page();
        }

        public string FormatJson(string json)
        {
            try
            {
                var parsed = JsonDocument.Parse(json);
                return JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return json;
            }
        }
    }
}
