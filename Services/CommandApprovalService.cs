namespace eZBERP_AI_IDE.Services;

public sealed class CommandApprovalService
{
    private static readonly string[] SafePrefixes =
    {
        "sf org list",
        "git status",
        "git diff",
        "git branch"
    };

    private static readonly string[] ApprovalPrefixes =
    {
        "sf project deploy start",
        "sf project retrieve start",
        "sf apex run test",
        "sf data query"
    };

    private static readonly string[] BlockedFragments =
    {
        "rm ",
        "del ",
        "git reset --hard",
        "git clean",
        "sf org delete"
    };

    public CommandApprovalRequest Evaluate(string commandText, string? targetOrg = null)
    {
        var normalized = commandText.Trim();

        if (BlockedFragments.Any(fragment => normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return new CommandApprovalRequest(normalized, "Blocked command", true, true, "This command is blocked by the safety policy.", "Blocked");
        }

        if (SafePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return new CommandApprovalRequest(normalized, "Low-risk allowed command", false, false, string.Empty, "Safe");
        }

        if (ApprovalPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var risk = IsProductionLike(targetOrg) ? "High" : "Medium";
            var reason = IsProductionLike(targetOrg)
                ? "This command targets an org that looks like production and requires explicit approval."
                : "This command changes org state and requires explicit approval.";
            return new CommandApprovalRequest(normalized, "Approval required", true, false, reason, risk);
        }

        return new CommandApprovalRequest(normalized, "Unknown command", true, true, "This command is outside the allowlist and has been blocked.", "Blocked");
    }

    private static bool IsProductionLike(string? targetOrg)
    {
        if (string.IsNullOrWhiteSpace(targetOrg))
        {
            return false;
        }

        return targetOrg.Contains("prod", StringComparison.OrdinalIgnoreCase)
               || targetOrg.Contains("production", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record CommandApprovalRequest(
    string CommandText,
    string Description,
    bool RequiresApproval,
    bool IsBlocked,
    string Reason,
    string RiskLevel);
