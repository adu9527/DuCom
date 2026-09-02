using System.Xml.Linq;

namespace DuCom.Core.Tests.Architecture;

public sealed class LocalizationResourceTests
{
    [Fact]
    public void ChineseAndEnglishResourcesContainTheSameKeys()
    {
        string resourceDirectory = Path.Combine(FindSolutionRoot(), "src", "DuCom", "Resources", "Languages");
        string[] englishKeys = ReadKeys(Path.Combine(resourceDirectory, "en-US.xaml"));
        string[] chineseKeys = ReadKeys(Path.Combine(resourceDirectory, "zh-CN.xaml"));

        Assert.Equal(englishKeys, chineseKeys);
    }

    [Fact]
    public void OnlySupportedLanguageDictionariesExist()
    {
        string resourceDirectory = Path.Combine(FindSolutionRoot(), "src", "DuCom", "Resources", "Languages");
        string[] files = Directory.GetFiles(resourceDirectory, "*.xaml")
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        Assert.Equal(["en-US", "zh-CN"], files);
    }

    [Theory]
    [InlineData("en-US.xaml")]
    [InlineData("zh-CN.xaml")]
    public void LanguageDictionariesContainNoDuplicateKeys(string fileName)
    {
        string path = Path.Combine(FindSolutionRoot(), "src", "DuCom", "Resources", "Languages", fileName);

        string[] keys = ReadKeys(path);

        Assert.DoesNotContain(keys.GroupBy(key => key, StringComparer.Ordinal), group => group.Count() > 1);
    }

    private static string[] ReadKeys(string path)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path)
            .Descendants()
            .Select(element => (string?)element.Attribute(xaml + "Key"))
            .Where(key => key is not null)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
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
}
