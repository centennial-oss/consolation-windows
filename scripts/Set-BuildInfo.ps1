param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [string] $Version = "localdev",
    [string] $BuildType = "Debug",
    [string] $BuildDate = "localdev",
    [string] $CommitId = "localdev",
    [string] $Architecture = "localdev"
)

$content = @"
namespace Consolation
{
    internal static class BuildInfo
    {
        public const string Version = "$Version";
        public const string BuildType = "$BuildType";
        public const string BuildDate = "$BuildDate";
        public const string CommitId = "$CommitId";
        public const string Architecture = "$Architecture";

        public static string CopyableBlob =>
            `$"Version: {Version} ({Architecture})\n" +
            `$"Build Type: {BuildType}\n" +
            `$"Date: {BuildDate}\n" +
            `$"Commit: {CommitId}";
    }
}
"@

Set-Content -LiteralPath $Path -Value $content -NoNewline
