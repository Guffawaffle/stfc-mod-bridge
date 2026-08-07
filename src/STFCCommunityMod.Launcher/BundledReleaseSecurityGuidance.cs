using System.IO;
using System.Reflection;

namespace STFCCommunityMod.Launcher;

internal sealed record ReleaseSecurityGuidance(
    string IndependentVerification,
    string CompromiseResponse);

internal static class BundledReleaseSecurityGuidance
{
    internal const string VerificationResourceName =
        "STFCCommunityMod.Launcher.Guidance.IndependentVerification.md";
    internal const string ResponseResourceName =
        "STFCCommunityMod.Launcher.Guidance.CompromiseResponse.md";

    public static ReleaseSecurityGuidance Load()
    {
        var assembly = typeof(BundledReleaseSecurityGuidance).Assembly;
        return new(
            LoadText(assembly, VerificationResourceName),
            LoadText(assembly, ResponseResourceName));
    }

    private static string LoadText(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"The bundled guidance resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"The bundled guidance resource '{resourceName}' is empty.");
        }

        return text;
    }
}
