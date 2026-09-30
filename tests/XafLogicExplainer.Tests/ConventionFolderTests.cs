using XafLogicExplainer.Core.Generators;
using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Tests;

/// <summary>
/// The folders the index tells an agent to put code in, named the way its citations name files.
/// </summary>
/// <remarks>
/// A controller in the platform project beside the module sits outside the module's folder. Its
/// folder was then printed as the absolute path of whichever machine ran the extraction, in a file
/// meant to be committed: wrong on every other machine, and a diff every time someone else
/// regenerated it — next to citations of the same controllers that were relative.
/// </remarks>
public class ConventionFolderTests
{
    private static readonly string Solution = Path.Combine(Path.GetTempPath(), "xle-folders", "App");
    private static readonly string Module = Path.Combine(Solution, "App.Module");

    [Fact]
    public void TheModuleFolderIsRelativeToTheModule() =>
        Assert.Contains("`Controllers/`", ControllersLine);

    [Fact]
    public void ThePlatformFolderIsNamedTheWayItsFilesAreCited() =>
        Assert.Contains("`../PharmacyDemo.Blazor.Server/Controllers/`", ControllersLine);

    [Fact]
    public void TheRecipeForAnActionNamesBothFolders()
    {
        var step = Index.Split('\n').Single(line => line.Contains("Put the controller in", StringComparison.Ordinal));

        Assert.Contains("`Controllers/`", step);
        Assert.Contains("`../PharmacyDemo.Blazor.Server/Controllers/`", step);
    }

    [Fact]
    public void NoFolderIsAPathOnThisMachine() =>
        Assert.DoesNotContain(Path.GetDirectoryName(SampleProjects.DemoPath)!.Replace('\\', '/'),
            Index.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void AnEntityFolderInAPlatformProjectIsNamedFromTheSolution()
    {
        var project = Project(Module, entityFiles: [Path.Combine(Solution, "App.Blazor.Server", "BusinessObjects", "Setting.cs")]);

        Assert.Equal("../App.Blazor.Server/BusinessObjects", CodebaseConventions.Infer(project).EntityFolder);
    }

    [Fact]
    public void AFileInTheProjectFolderItselfHasNoFolder()
    {
        var project = Project(Module, controllerFiles: [Path.Combine(Module, "RootController.cs")]);

        Assert.Empty(CodebaseConventions.Infer(project).ControllerFolders);
    }

    [Fact]
    public void AFolderOutsideProjectAndSolutionIsNotReported()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "xle-folders", "Shared", "Controllers", "SharedController.cs");
        var project = Project(Module, controllerFiles: [elsewhere]);

        Assert.Empty(CodebaseConventions.Infer(project).ControllerFolders);
    }

    [Fact]
    public void TheFolderIsTheSameWhicheverSeparatorTheProjectPathWasTypedWith()
    {
        var file = Path.Combine(Solution, "App.Blazor.Server", "Controllers", "GridController.cs");

        var typedWithForwardSlashes = Project(Module.Replace('\\', '/'), controllerFiles: [file]);
        var typedWithTheSystemSeparator = Project(Module, controllerFiles: [file]);

        Assert.Equal(["../App.Blazor.Server/Controllers"], CodebaseConventions.Infer(typedWithForwardSlashes).ControllerFolders);
        Assert.Equal(["../App.Blazor.Server/Controllers"], CodebaseConventions.Infer(typedWithTheSystemSeparator).ControllerFolders);
    }

    [Fact]
    public void AProjectAtTheRootOfADriveKeepsItsFolders()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var project = Project(root, controllerFiles: [Path.Combine(root, "Controllers", "RootDriveController.cs")]);

        Assert.Equal(["Controllers"], CodebaseConventions.Infer(project).ControllerFolders);
    }

    private static ExtractedProject Project(
        string projectPath,
        string[]? entityFiles = null,
        string[]? controllerFiles = null) => new()
    {
        ProjectPath = projectPath,
        Entities = [.. (entityFiles ?? []).Select(file => new ExtractedEntity { ClassName = Path.GetFileNameWithoutExtension(file), FilePath = file })],
        Controllers = [.. (controllerFiles ?? []).Select(file => new ExtractedController { ClassName = Path.GetFileNameWithoutExtension(file), FilePath = file })],
    };

    private static string Index => new AgentContextGenerator().GenerateIndex(SampleProjects.Demo, []);

    private static string ControllersLine =>
        Index.Split('\n').Single(line => line.StartsWith("- **Controllers live in:**", StringComparison.Ordinal));
}
