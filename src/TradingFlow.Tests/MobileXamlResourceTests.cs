using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TradingFlow.Tests;

public sealed class MobileXamlResourceTests
{
    [Fact]
    public void EveryStaticResourceReferenceResolvesBeforeAndroidNavigation()
    {
        var root = Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Mobile");
        var files = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "obj" or "bin")).ToArray();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2009/xaml";
        var documents = files.Select(path => (path, document: XDocument.Load(path))).ToArray();
        var keys = documents.SelectMany(item => item.document.Descendants().Attributes(xaml + "Key"))
            .Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        var unresolved = documents.SelectMany(item => item.document.Descendants().Attributes()
            .SelectMany(attribute => Regex.Matches(attribute.Value, @"\{StaticResource\s+([\w]+)")
                .Select(match => (item.path, key: match.Groups[1].Value))))
            .Where(item => !keys.Contains(item.key))
            .Select(item => Path.GetRelativePath(root, item.path) + ": " + item.key).ToArray();
        Assert.NotEmpty(files);
        Assert.Empty(unresolved);
    }
}
