using System.Text.RegularExpressions;
using System.Xml.Linq;
using EqLog.Application.Inventory;

namespace EqLog.Infrastructure.Tests;

public sealed class LayerBoundaryTests
{
    [Fact]
    public void RuntimeReferencesPointOnlyInward()
    {
        Assert.DoesNotContain(typeof(LogEngine).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "EqLog.Application" or "EqLog.Infrastructure" or "EqLogOverlay" or "Polly.Core" or
                "System.Net.Http" or "PresentationFramework" or "WindowsBase");
        Assert.DoesNotContain(typeof(InventoryService).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "EqLog.Infrastructure" or "EqLogOverlay" or "System.Net.Http" or "PresentationFramework" or "WindowsBase");
    }

    [Theory]
    [InlineData("EqLog.Core", 0)]
    [InlineData("EqLog.Application", 1)]
    [InlineData("EqLog.Infrastructure", 2)]
    public void ProjectReferencesFollowTheLayerGraph(string project, int expectedCount)
    {
        string root = FindRoot();
        var document = XDocument.Load(Path.Combine(root, "src", project, project + ".csproj"));
        var references = document.Descendants("ProjectReference").Select(reference =>
            Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value)).Order().ToArray();
        Assert.Equal(expectedCount, references.Length);
        Assert.All(references, reference => Assert.Contains(reference, new[] { "EqLog.Core", "EqLog.Application" }));
        if (project == "EqLog.Core")
        {
            Assert.Empty(document.Descendants("PackageReference"));
        }
        var domainTests = XDocument.Load(Path.Combine(root, "tests", "EqLog.Core.Tests", "EqLog.Core.Tests.csproj"));
        Assert.Equal("EqLog.Core", Path.GetFileNameWithoutExtension(
            Assert.Single(domainTests.Descendants("ProjectReference")).Attribute("Include")!.Value));
    }

    [Theory]
    [InlineData("EqLog.Core")]
    [InlineData("EqLog.Application")]
    public void DomainAndApplicationContainNoConcreteFileHttpOrWpfAccess(string project)
    {
        foreach (string path in SourceFiles(Path.Combine(FindRoot(), "src", project)))
        {
            string code = StripComments(File.ReadAllText(path));
            Assert.False(Regex.IsMatch(code,
                @"(?<!\.)\b(File|Directory)\s*\.|\b(FileInfo|DirectoryInfo|FileStream|HttpClient|HttpRequestMessage|HttpResponseMessage|HttpMessageHandler|StreamReader|StreamWriter)\b"),
                path + " crosses the file/HTTP boundary.");
            Assert.DoesNotContain("using System.Windows", code);
            Assert.DoesNotContain("using EqLog.Infrastructure", code);
        }
    }

    [Fact]
    public void ViewsDoNotAccessConcreteAdaptersOrFiles()
    {
        foreach (string path in SourceFiles(Path.Combine(FindRoot(), "src", "EqLog.Overlay")))
        {
            if (Path.GetFileName(path).EndsWith("Composition.cs", StringComparison.Ordinal))
            {
                continue;
            }
            string code = StripComments(File.ReadAllText(path));
            Assert.DoesNotContain("using EqLog.Infrastructure", code);
            Assert.False(Regex.IsMatch(code, @"\b(File|Directory)\s*\.|\b(AtomicFile|JsonLines|HttpClient|FileStream)\b"),
                path + " accesses concrete file/storage/HTTP adapters outside composition.");
        }
    }

    private static IEnumerable<string> SourceFiles(string folder)
    {
        return Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
    }
    private static string StripComments(string code)
    {
        return Regex.Replace(code, @"//[^\r\n]*|/\*[\s\S]*?\*/", "");
    }
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EqLogOverlay.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Architecture checks require the repository checkout.");
    }
}
