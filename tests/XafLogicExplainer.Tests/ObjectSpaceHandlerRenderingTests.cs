using XafLogicExplainer.Core.Generators;
using XafLogicExplainer.Mcp;
using XafLogicExplainer.Mcp.Tools;

namespace XafLogicExplainer.Tests;

/// <summary>
/// Handlers attached to Object Space events, wherever the rules are shown.
/// </summary>
/// <remarks>
/// Where rules are listed once, a handler is listed under each class it names, under the outside type a
/// controller targets, or as tied to no class. Where the reader is looking at one class, it shows every
/// handler that runs for that class — including those for a class it derives from or an interface it
/// implements.
/// </remarks>
public class ObjectSpaceHandlerRenderingTests
{
    [Fact]
    public void TheRulesDocumentListsAHandlerUnderTheClassItNames()
    {
        var rules = Section("en", "_BusinessRules");

        Assert.Contains("## Logic Attached to the Object Space", rules);
        Assert.Contains(
            "- **before a save**: `OrderController.ObjectSpace_Committing`, controller for `Order`, checks `Order` — `Controllers/OrderController.cs:12`",
            Block(rules, "### Order\n"));
    }

    [Fact]
    public void TheRulesDocumentGroupsAnInterfaceTargetAndTheUnattached()
    {
        var rules = Section("en", "_BusinessRules");

        Assert.Contains("`AuditController.ObjectSpace_ObjectSaved`", Block(rules, "### Every class built on `IAudited`\n"));
        Assert.Contains("`PlainViewController.OnDeleting`", Block(rules, "### Not tied to a business class\n"));
        Assert.DoesNotContain("built on `Note`", rules);
    }

    [Fact]
    public void TheRulesDocumentMarksAReceiverItCouldNotConfirm() =>
        Assert.Contains("- **before a save**: `XpoBits` (lambda), checks `Order`, object space not confirmed from the source — ",
            Section("en", "_BusinessRules"));

    [Fact]
    public void AnEntityPageShowsTheHandlersOfItsBaseClassAndItsInterface()
    {
        var entities = Section("en", "_Entities");

        Assert.Contains("`VehicleController` (lambda), controller for `Vehicle`", Block(entities, "## Car\n"));
        Assert.Contains("`AuditController.ObjectSpace_ObjectSaved`", Block(entities, "## Order\n"));
        Assert.DoesNotContain("AuditController", Block(entities, "## Invoice\n"));
    }

    [Fact]
    public void TheSpanishDocumentsSayItInSpanish()
    {
        var rules = Section("es", "_BusinessRules");

        Assert.Contains("## Logica Enganchada al Object Space", rules);
        Assert.Contains("- **antes de guardar**: `OrderController.ObjectSpace_Committing`, controlador de `Order`, comprueba `Order`", rules);
        Assert.Contains("### Sin clase de negocio", rules);
    }

    [Fact]
    public void TheLifecycleSectionNoLongerSaysHandlersAreNotRead()
    {
        var rules = Section("en", "_BusinessRules");

        Assert.DoesNotContain("a controller handling `ObjectSpace.Committing`, a job) is not", rules);
        Assert.Contains("logic that changes objects without such an event, in a job or a service, is not listed", rules);
    }

    [Fact]
    public void TheIndexSaysHandlersAreListedAndWhatIsStillNot()
    {
        var index = new AgentContextGenerator().GenerateIndex(SampleProjects.ObjectSpaceHandlers, []);

        Assert.Contains("handlers attached to Object Space events", index);
        Assert.Contains("is not inventoried, so not finding it proves nothing.", index);
    }

    [Fact]
    public async Task TheRulesToolShowsAClassWhoseOnlyLogicIsAHandler()
    {
        var result = await Detail.RulesAsync("Invoice", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("### Attached to the Object Space", result);
        Assert.Contains("- after a save: `InvoiceController.Committed`, controller for `Invoice`", result);
    }

    [Fact]
    public async Task TheRulesToolShowsAClassTheHandlerOfItsBase() =>
        Assert.Contains("`VehicleController` (lambda), controller for `Vehicle`",
            await Detail.RulesAsync("Car", cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public async Task TheApplicationsRuleSetListsTheOutsideTargetAndTheUnattached()
    {
        var result = await Detail.RulesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("## Every class built on `IAudited`", result);
        Assert.Contains("## Not tied to a business class", result);
        Assert.Contains("`PlainViewController.OnDeleting`", result);
    }

    [Fact]
    public async Task TheEntityToolShowsItsHandlers() =>
        Assert.Contains("`OrderController.ObjectSpace_Committing`",
            await Detail.EntityAsync("Order", cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public async Task SearchFindsAHandlerByItsName() =>
        Assert.Contains("**handler** `OrderController.ObjectSpace_Committing`, controller for `Order`, checks `Order` — runs before a save",
            await Discovery.SearchAsync("ObjectSpace_Committing", cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void TheExplainPageShowsAHandlerOnlyClassAndTheUnattached()
    {
        var page = new HtmlExplainerGenerator("0.17.2").Generate(SampleProjects.ObjectSpaceHandlers);

        Assert.Contains("<th>Attached to the Object Space</th><th>Handler</th>", page);
        Assert.Contains("<span class=\"mono\">InvoiceController.Committed</span>, controller for <span class=\"mono\">Invoice</span>", page);
        Assert.Contains("<span class=\"card__name\">Not tied to a business class</span>", page);
    }

    /// <summary>The text from a heading to the next heading of the same level or higher.</summary>
    private static string Block(string document, string heading)
    {
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No '{heading.Trim()}' in the document.");
        var level = heading.TakeWhile(c => c == '#').Count();
        var rest = document[(start + heading.Length)..];
        var end = Enumerable.Range(1, level)
            .Select(n => rest.IndexOf("\n" + new string('#', n) + " ", StringComparison.Ordinal))
            .Where(i => i >= 0)
            .DefaultIfEmpty(rest.Length)
            .Min();
        return rest[..end];
    }

    private static string Section(string language, string suffix) =>
        new MarkdownDocumentationGenerator(language)
            .GenerateSections(SampleProjects.ObjectSpaceHandlers)
            .Single(section => section.FileName.EndsWith(suffix, StringComparison.Ordinal))
            .Content
            .Replace("\r", "");

    private static XafProjectContext Context => new(
    [
        new XafProjectSource { Name = "Shop", Path = SampleProjects.ObjectSpaceHandlerPath, Language = "en" },
    ]);

    private static XafDiscoveryTools Discovery => new(Context);

    private static XafDetailTools Detail => new(Context);
}
