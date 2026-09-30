using XafLogicExplainer.Core.Generators;
using XafLogicExplainer.Mcp;
using XafLogicExplainer.Mcp.Tools;

namespace XafLogicExplainer.Tests;

/// <summary>
/// Methods a class runs on create, load and save, where the rules are shown.
/// </summary>
/// <remarks>
/// Every output that lists what the application enforces — the business rules document, each entity's
/// page, the MCP rules and search tools, the explain page — says the same thing about them: each hook once
/// under the class that declared it where rules are listed once, and under every class it runs for where
/// the reader is looking at one class. None of them claims more than the class itself declares.
/// </remarks>
public class LifecycleRenderingTests
{
    [Fact]
    public void TheRulesDocumentListsEachHookOnceUnderItsDeclarer()
    {
        var rules = Section("en", "_BusinessRules");

        Assert.Contains("## Logic on Create, Load and Save", rules);
        Assert.Contains("### AuditedObject\n- **when saved**: `OnSaving`, assigns `ChangedOn`, `ChangedBy` — `BusinessObjects/AuditedObject.cs:12`", rules);
        Assert.DoesNotContain("### Car\n", rules);
    }

    [Fact]
    public void TheRulesDocumentNamesAHookOnABorrowedBaseOnce()
    {
        var rules = Section("en", "_BusinessRules");

        Assert.Single(rules.Split('\n'), line => line == "### Tracking.Core.TrackedRecord");
    }

    [Fact]
    public void TheRulesDocumentTellsApartTwoDeclarersOfOneName()
    {
        var rules = Section("en", "_BusinessRules");

        Assert.Contains("### Garage.Module.BusinessObjects.Archive.TrackedRecord\n- **when saved**: `OnSaving`, assigns `Shelf`", rules);
        Assert.Contains("### Tracking.Core.TrackedRecord\n- **when saved**: `OnSaving`, assigns `TrackedOn`", rules);
    }

    [Fact]
    public void TheRulesDocumentSaysWhatItDoesNotList() =>
        Assert.Contains("in a job or a service, is not listed: a class missing from both may still change when it is saved",
            Section("en", "_BusinessRules"));

    [Fact]
    public void AnEntityPageShowsTheHookItInherits() =>
        Assert.Contains("- **when saved**: `OnSaving`, assigns `ChangedOn`, `ChangedBy` — `BusinessObjects/AuditedObject.cs:12` — inherited from `AuditedObject`",
            Section("en", "_Entities"));

    [Fact]
    public void TheSpanishDocumentsSayItInSpanish()
    {
        var rules = Section("es", "_BusinessRules");

        Assert.Contains("## Logica al Crear, Cargar y Guardar", rules);
        Assert.Contains("- **al guardar**: `OnSaving`, asigna `ChangedOn`, `ChangedBy`", rules);
    }

    [Fact]
    public async Task TheRulesToolShowsAnEntityTheHookItInherits()
    {
        var result = await Detail.RulesAsync("Car", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("### Runs when created, loaded or saved", result);
        Assert.Contains("- when saved: `OnSaving`, assigns `ChangedOn`, `ChangedBy`", result);
        Assert.Contains("(inherited from `AuditedObject`)", result);
    }

    [Fact]
    public async Task TheRulesToolListsABorrowedBaseForTheWholeApplication()
    {
        var result = await Detail.RulesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("## Tracking.Core.TrackedRecord", result);
        Assert.DoesNotContain("## Car\n", result.Replace("\r", ""));
    }

    [Fact]
    public async Task TheRulesToolSaysAClassWithNoHookHasNone()
    {
        var result = await Detail.RulesAsync("Wheel", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("`Wheel` declares no rule that validates, styles or calculates and no method run on create, load or save", result);
        Assert.Contains("Logic attached from outside the class is not read.", result);
    }

    [Fact]
    public async Task SearchFindsAHookByWhatItAssigns()
    {
        var result = await Discovery.SearchAsync("ChangedOn", kind: "rule", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("**rule** `AuditedObject` OnSaving — runs when saved, assigns `ChangedOn`, `ChangedBy`", result);
    }

    [Fact]
    public void TheExplainPageShowsWhereAHookComesFrom()
    {
        var page = new HtmlExplainerGenerator("0.17.2").Generate(SampleProjects.Lifecycle);

        Assert.Contains("<th>Runs</th><th>Method</th><th>Assigns</th>", page);
        Assert.Contains("from <span class=\"mono\">AuditedObject</span>", page);
    }

    [Fact]
    public void TheIndexDoesNotClaimSaveTimeLogicIsComplete() =>
        Assert.Contains(
            "That completeness does not extend to what happens when an object is saved.",
            new AgentContextGenerator().GenerateIndex(SampleProjects.Lifecycle, []));

    private static string Section(string language, string suffix) =>
        new MarkdownDocumentationGenerator(language)
            .GenerateSections(SampleProjects.Lifecycle)
            .Single(section => section.FileName.EndsWith(suffix, StringComparison.Ordinal))
            .Content
            .Replace("\r", "");

    private static XafProjectContext Context => new(
    [
        new XafProjectSource { Name = "Garage", Path = SampleProjects.LifecyclePath, Language = "en" },
    ]);

    private static XafDiscoveryTools Discovery => new(Context);

    private static XafDetailTools Detail => new(Context);
}
