using System.Reflection;
using System.Xml.Linq;

namespace DuCom.Core.Tests.Architecture;

public sealed class DependencyGuardTests
{
    private static readonly string[] WpfAssemblyNames =
    [
        "PresentationCore",
        "PresentationFramework",
        "ReachFramework",
        "System.Printing",
        "System.Xaml",
        "UIAutomationClient",
        "UIAutomationTypes",
        "WindowsBase",
    ];

    [Fact]
    public void CoreProjectTargetsUiFreeFrameworkAndDisablesWpf()
    {
        XDocument project = LoadProject("src", "DuCom.Core", "DuCom.Core.csproj");

        Assert.Equal("net10.0", GetProperty(project, "TargetFramework"));
        Assert.False(string.Equals(GetProperty(project, "UseWPF"), "true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            project.Descendants("FrameworkReference"),
            reference => string.Equals(
                (string?)reference.Attribute("Include"),
                "Microsoft.WindowsDesktop.App.WPF",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CoreAssemblyDoesNotReferenceWpfAssemblies()
    {
        string[] references = Assembly.Load("DuCom.Core")
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, reference => WpfAssemblyNames.Contains(reference));
    }

    [Fact]
    public void TestProjectReferencesCoreButNotWpfApplication()
    {
        XDocument project = LoadProject("tests", "DuCom.Core.Tests", "DuCom.Core.Tests.csproj");
        string[] references = project.Descendants("ProjectReference")
            .Select(reference => ((string?)reference.Attribute("Include") ?? string.Empty)
                .Replace('/', '\\'))
            .ToArray();

        Assert.Contains("..\\..\\src\\DuCom.Core\\DuCom.Core.csproj", references);
        Assert.DoesNotContain(
            references,
            reference => reference.EndsWith("\\src\\DuCom\\DuCom.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("net10.0", GetProperty(project, "TargetFramework"));
        Assert.False(string.Equals(GetProperty(project, "UseWPF"), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] ApprovedApplicationSourceLinks =
    [
        "Behaviors\\CoalescedActionGate.cs",
        "Services\\Shortcuts\\ShortcutModifiers.cs",
        "Services\\Shortcuts\\ShortcutKeyGesture.cs",
        "Services\\Shortcuts\\ShortcutAction.cs",
        "Services\\Shortcuts\\ShortcutDefinition.cs",
        "Services\\Shortcuts\\ShortcutConflictResult.cs",
        "Services\\Shortcuts\\ShortcutConfiguration.cs",
        "Services\\Shortcuts\\ShortcutManager.cs",
    ];

    [Fact]
    public void TestProjectApplicationSourceLinksStayWithinApprovedBoundary()
    {
        // The test project compiles a frozen list of pure application-layer sources via
        // <Compile Include> links (ADR-0003). No new links may be added silently.
        XDocument project = LoadProject("tests", "DuCom.Core.Tests", "DuCom.Core.Tests.csproj");
        string[] linkedSources = project
            .Descendants("Compile")
            .Select(element => ((string?)element.Attribute("Include") ?? string.Empty).Replace('/', '\\'))
            .Where(include => include.Contains("..\\..\\src\\DuCom\\", StringComparison.OrdinalIgnoreCase))
            .Select(include =>
            {
                int marker = include.IndexOf("src\\DuCom\\", StringComparison.OrdinalIgnoreCase);
                return include[(marker + "src\\DuCom\\".Length)..];
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] expected = [.. ApprovedApplicationSourceLinks.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        Assert.Equal(expected, linkedSources);
    }

    private static XDocument LoadProject(params string[] relativeSegments)
    {
        string solutionRoot = FindSolutionRoot();
        return XDocument.Load(Path.Combine([solutionRoot, .. relativeSegments]));
    }

    private static string FindSolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DuCom.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate DuCom.slnx from the test output directory.");
    }

    private static string? GetProperty(XDocument project, string propertyName) =>
        project.Descendants(propertyName).Select(element => element.Value).FirstOrDefault();
}
