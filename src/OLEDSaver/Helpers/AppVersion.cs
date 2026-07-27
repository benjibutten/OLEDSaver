using System.Reflection;

namespace OLEDSaver.Helpers;

public static class AppVersion
{
    /// <summary>Version as built, without the "+&lt;commit&gt;" suffix the SDK appends.</summary>
    public static string DisplayText { get; } = Resolve();

    private static string Resolve()
    {
        string? informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
            return "v1.0.0";

        int plus = informational.IndexOf('+');
        string version = plus >= 0 ? informational[..plus] : informational;
        return $"v{version}";
    }
}
