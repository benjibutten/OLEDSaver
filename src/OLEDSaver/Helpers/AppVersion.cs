using System.Reflection;

namespace OLEDSaver.Helpers;

public static class AppVersion
{
    /// <summary>
    /// The stamped assembly version, or the placeholder 1.0.0.0 that a local build
    /// carries. The updater uses this rather than <see cref="DisplayText"/> because it
    /// has to compare against release tags.
    /// </summary>
    public static Version? Current { get; } = typeof(AppVersion).Assembly.GetName().Version;

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
