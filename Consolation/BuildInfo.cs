namespace Consolation
{
    internal static class BuildInfo
    {
        public const string Version = "localdev";
        public const string BuildType = "Debug";
        public const string BuildDate = "localdev";
        public const string CommitId = "localdev";
        public const string Architecture = "localdev";

        public static string CopyableBlob =>
            $"Version: {Version} (Windows, {Architecture})\n" +
            $"Build Type: {BuildType}\n" +
            $"Date: {BuildDate}\n" +
            $"Commit: {CommitId}";
    }
}