using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KHZ.Tools.Safety;

/// <summary>Outcome of static risk analysis of a proposed shell command.</summary>
/// <param name="Blocked">The command is refused before any prompt is shown.</param>
/// <param name="BlockReason">Machine-readable refusal code, when blocked.</param>
/// <param name="Level">"low", "elevated", or "dangerous".</param>
/// <param name="Flags">Human-readable risk flags to surface in the prompt.</param>
public sealed record CommandAssessment(
    bool Blocked,
    string? BlockReason,
    string Level,
    string[] Flags);

/// <summary>
/// Static classifier for proposed PowerShell commands.
/// </summary>
/// <remarks>
/// This closes a real hole in the previous design: every filesystem guard
/// (relative-only paths, containment, reparse rejection, <c>.khz</c> blocking)
/// was bypassable because a shell tool could reach any path on the machine, and
/// the only control was a human approving raw command text.
/// <para>
/// Three layers now apply, in order:
/// </para>
/// <list type="number">
/// <item>an optional allowlist (<c>KHZ_TOOLS_PS_ALLOWLIST</c>) — when set, the
/// command must match one of its regexes or it is refused;</item>
/// <item>a catastrophic-command blocklist that is refused before any prompt is
/// rendered, so approval fatigue cannot authorise it;</item>
/// <item>risk flags attached to the confirmation prompt so the human sees *why*
/// the command is dangerous rather than only *what* it says.</item>
/// </list>
/// <para>
/// Static analysis of shell text is inherently incomplete: obfuscation,
/// encoded commands, and indirection can evade any pattern set. This is
/// defence in depth, not a sandbox. The authoritative control remains the
/// human confirmation and the non-elevated process boundary.
/// </para>
/// </remarks>
public static class CommandRisk
{
    /// <summary>Set to <c>1</c> to permit otherwise-blocked destructive commands.</summary>
    public const string OverrideVariable = "KHZ_TOOLS_ALLOW_DESTRUCTIVE";

    /// <summary>Semicolon-separated regex allowlist. When set, acts as a whitelist.</summary>
    public const string AllowlistVariable = "KHZ_TOOLS_PS_ALLOWLIST";

    // Irreversible or machine-scope operations. Refused before prompting.
    private static readonly (string Pattern, string Reason)[] Catastrophic =
    [
        (@"\bformat-volume\b", "volume_format"),
        (@"\bclear-disk\b", "disk_clear"),
        (@"\binitialize-disk\b", "disk_initialize"),
        (@"\bvssadmin\b.*\bdelete\b", "shadow_copy_deletion"),
        (@"\bwbadmin\b.*\bdelete\b", "backup_deletion"),
        (@"\bbcdedit\b", "boot_configuration"),
        (@"\bcipher\b\s*/w", "free_space_wipe"),
        (@"\bset-mppreference\b", "defender_configuration"),
        (@"\bremove-mpthreat\b", "defender_configuration"),
        (@"\bset-executionpolicy\b", "execution_policy_change"),
        (@"\bnetsh\b.*\bfirewall\b", "firewall_configuration"),
        (@"\bset-netfirewall", "firewall_configuration"),
        (@"\bnew-localuser\b", "account_creation"),
        (@"\badd-localgroupmember\b", "privilege_grant"),
        (@"\bnet\s+user\b.*\/add", "account_creation"),
        (@"\bstop-computer\b", "host_shutdown"),
        (@"\brestart-computer\b", "host_restart"),
        (@"\bremove-item\b(?=.*\s-recurse\b)(?=.*\s-force\b)(?=.*[\\\/](?:windows|program files|users)\b)",
            "system_path_recursive_delete")
    ];

    // Elevated-risk operations. Prompted, but flagged explicitly.
    private static readonly (string Pattern, string Flag)[] Flagged =
    [
        (@"\bremove-item\b", "deletes files"),
        (@"\bremove-itemproperty\b", "deletes registry values"),
        (@"\bri\b\s", "deletes files (alias)"),
        (@"\bdel\b\s", "deletes files (alias)"),
        (@"\brd\b\s", "removes directories (alias)"),
        (@"\brmdir\b", "removes directories"),
        (@"\binvoke-webrequest\b", "network egress"),
        (@"\binvoke-restmethod\b", "network egress"),
        (@"\b(?:iwr|irm)\b", "network egress (alias)"),
        (@"\bcurl\b", "network egress"),
        (@"\bwget\b", "network egress"),
        (@"\bstart-bitstransfer\b", "network egress"),
        (@"\bnew-object\s+net\.webclient\b", "network egress"),
        (@"\binvoke-expression\b", "dynamic code execution"),
        (@"\biex\b", "dynamic code execution (alias)"),
        (@"\b-encodedcommand\b", "encoded command payload"),
        (@"\bfrombase64string\b", "encoded payload decoding"),
        (@"\bstart-process\b", "launches another process"),
        (@"\bcertutil\b", "can download or decode payloads"),
        (@"\bbitsadmin\b", "can download payloads"),
        (@"\bmshta\b", "script host execution"),
        (@"\brundll32\b", "arbitrary library execution"),
        (@"\bregsvr32\b", "arbitrary library registration"),
        (@"\bschtasks\b", "scheduled task change"),
        (@"\bregister-scheduledtask\b", "scheduled task change"),
        (@"\bnew-service\b", "service creation"),
        (@"\bset-service\b", "service change"),
        (@"\bstop-service\b", "service stop"),
        (@"\breg(?:\.exe)?\s+(?:add|delete)\b", "registry mutation"),
        (@"\bset-itemproperty\b.*\bhk(?:lm|cu)\b", "registry mutation"),
        (@"\btakeown\b", "ownership change"),
        (@"\bicacls\b", "permission change"),
        (@"\bset-acl\b", "permission change"),
        (@"\bgit\b\s+push\b", "publishes commits to a remote"),
        (@"\bgit\b\s+reset\b.*--hard\b", "discards local work"),
        (@"\bgit\b\s+clean\b.*-[a-z]*f", "deletes untracked files"),
        (@"\.\.[\\\/]", "references a parent directory outside the workspace")
    ];

    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Classifies a command without executing it.</summary>
    public static CommandAssessment Assess(string command)
    {
        var text = (command ?? string.Empty).Trim();

        if (text.Length == 0)
            return new CommandAssessment(true, "empty_command", "dangerous", []);

        var allowlist = ReadAllowlist();

        if (allowlist.Count > 0
            && !allowlist.Any(pattern => Regex.IsMatch(text, pattern, Options)))
        {
            return new CommandAssessment(
                true,
                "not_in_allowlist",
                "dangerous",
                ["An allowlist is configured and this command does not match it."]);
        }

        var overridden = string.Equals(
            Environment.GetEnvironmentVariable(OverrideVariable),
            "1",
            StringComparison.Ordinal);

        foreach (var (pattern, reason) in Catastrophic)
        {
            if (!Regex.IsMatch(text, pattern, Options))
                continue;

            if (!overridden)
            {
                return new CommandAssessment(
                    true,
                    reason,
                    "dangerous",
                    [
                        "Refused as irreversible or machine-scope: " + reason + ".",
                        "Set " + OverrideVariable + "=1 deliberately to permit this class."
                    ]);
            }

            return new CommandAssessment(
                false,
                null,
                "dangerous",
                [
                    "Destructive class permitted only by " + OverrideVariable + ": " + reason + "."
                ]);
        }

        var flags = Flagged
            .Where(entry => Regex.IsMatch(text, entry.Pattern, Options))
            .Select(entry => entry.Flag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var level = flags.Length switch
        {
            0 => "low",
            <= 2 => "elevated",
            _ => "dangerous"
        };

        return new CommandAssessment(false, null, level, flags);
    }

    private static List<string> ReadAllowlist()
    {
        var raw = Environment.GetEnvironmentVariable(AllowlistVariable);

        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
