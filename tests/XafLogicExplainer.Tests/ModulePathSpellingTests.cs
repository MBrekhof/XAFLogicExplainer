using XafLogicExplainer.Core.Analyzers;

namespace XafLogicExplainer.Tests;

/// <summary>
/// A module path means the same folder however it is typed.
/// </summary>
/// <remarks>
/// The platform projects beside a module were found from the path as typed. With a trailing separator the
/// "parent" was the module itself, so the Blazor and Win projects were never read; with forward slashes on
/// Windows the module never equalled its own directory listing and was read a second time as its own sibling.
/// </remarks>
public class ModulePathSpellingTests
{
    private static string Spelled(bool forwardSlashes, bool trailingSeparator)
    {
        var path = SampleProjects.ObjectSpaceHandlerPath;
        var separator = forwardSlashes ? '/' : Path.DirectorySeparatorChar;
        if (forwardSlashes)
            path = path.Replace(Path.DirectorySeparatorChar, '/');
        return trailingSeparator ? path + separator : path;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ThePlatformProjectBesideTheModuleIsItsOnlySibling(bool forwardSlashes, bool trailingSeparator) =>
        Assert.Equal(
            ["Shop.Blazor.Server"],
            SourceRoster.SiblingDirectories(Spelled(forwardSlashes, trailingSeparator)).Select(Path.GetFileName));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EveryHandlerIsReadOnce(bool forwardSlashes, bool trailingSeparator) =>
        Assert.Equal(
            SampleProjects.ObjectSpaceHandlers.ObjectSpaceHandlers.Count,
            SampleProjects.Extract(Spelled(forwardSlashes, trailingSeparator)).ObjectSpaceHandlers.Count);
}
