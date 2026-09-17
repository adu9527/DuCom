using System.IO;
using System.Xml.Linq;
using Xunit;

namespace DuCom.App.Tests;

public sealed class ContextMenuXamlTests
{
    [Fact]
    public void ContextMenusWithSeparatorsDoNotForceOneContainerStyle()
    {
        string sourceDirectory = Path.Combine(FindSolutionRoot(), "src", "DuCom");
        foreach (string path in Directory.GetFiles(sourceDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(path);
            foreach (XElement menu in document.Descendants().Where(element => element.Name.LocalName == "ContextMenu"))
            {
                bool hasSeparator = menu.Descendants().Any(element => element.Name.LocalName == "Separator");
                if (!hasSeparator) continue;

                Assert.DoesNotContain(menu.Attributes(), attribute => attribute.Name.LocalName == "ItemContainerStyle");
                Assert.DoesNotContain(menu.Elements(), element => element.Name.LocalName == "ContextMenu.ItemContainerStyle");
            }
        }
    }

    private static string FindSolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DuCom.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate DuCom.slnx.");
    }
}
