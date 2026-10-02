using System.Reflection;

namespace Familiar.Server.Infrastructure;

/// <summary>
/// What build this is. The Dockerfile passes VERSION and REVISION as build arguments, and the SDK
/// combines them into the informational version as <c>1.2.3+&lt;sha&gt;</c>. A plain local build
/// says <c>1.0.0</c> / <c>unknown</c> rather than claiming to be a release.
/// </summary>
public sealed record BuildInfo(string Version, string Revision)
{
    public static BuildInfo Current { get; } = Read();

    private static BuildInfo Read()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0
            ? new BuildInfo(informational, "unknown")
            : new BuildInfo(informational[..plus], informational[(plus + 1)..]);
    }
}
