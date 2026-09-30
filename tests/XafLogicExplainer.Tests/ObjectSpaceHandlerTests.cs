using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Tests;

/// <summary>
/// Logic attached to an Object Space event from outside the business class it concerns.
/// </summary>
/// <remarks>
/// XAF documents two places for save-time logic: the business class, and a controller handling
/// <c>ObjectSpace.Committing</c>, <c>ObjectChanged</c> and the like. Only the first was read, so an application
/// that validates or stamps its objects from controllers — or from generated rules hooked onto every Object
/// Space — was described as doing nothing on save.
/// </remarks>
public class ObjectSpaceHandlerTests
{
    private const string Ns = "Shop.Module.BusinessObjects";

    private static ExtractedProject P => SampleProjects.ObjectSpaceHandlers;

    [Fact]
    public void AControllerSubscriptionIsAttachedToItsTargetAndWhatItChecks()
    {
        var committing = Assert.Single(P.Handlers("OrderController"), h => h.Event == ObjectSpaceEvent.Committing);
        Assert.Equal("ObjectSpace_Committing", committing.Handler);
        Assert.True(committing.InController);
        Assert.Equal([$"{Ns}.Order"], committing.ControllerTargets);
        Assert.Equal([$"{Ns}.Order"], committing.Checks);
        Assert.False(committing.ReceiverUnconfirmed);
        Assert.Equal("OrderController.cs", Path.GetFileName(committing.FilePath));
        Assert.Equal(12, committing.Line);
    }

    [Fact]
    public void AResubscriptionIsReportedOnceAndAnUnsubscriptionNotAtAll()
    {
        Assert.Single(P.Handlers("OrderController"), h => h.Event == ObjectSpaceEvent.Committing);
        Assert.DoesNotContain(P.Handlers("XpoBits"), h => h.Handler == "Nothing");
    }

    [Fact]
    public void ALambdaHandlerIsReadForTypeTestsIncludingNotPatterns()
    {
        var changed = Assert.Single(P.Handlers("OrderController"), h => h.Event == ObjectSpaceEvent.ObjectChanged);
        Assert.Equal("lambda", changed.Handler);
        Assert.Equal(13, changed.Line);
        Assert.Equal([$"{Ns}.Invoice"], changed.Checks);
    }

    [Fact]
    public void ATypeTestInAMethodTheHandlerCallsCounts()
    {
        var rule = Assert.Single(P.Handlers("Rules"));
        Assert.False(rule.InController);
        Assert.Empty(rule.ControllerTargets);
        Assert.Equal([$"{Ns}.Order", $"{Ns}.Invoice"], rule.Checks);
    }

    [Fact]
    public void AnXpoSessionEventIsNotAnObjectSpaceEvent() =>
        Assert.DoesNotContain(P.Handlers("XpoBits"), h => h.Event == ObjectSpaceEvent.ObjectSaving);

    [Fact]
    public void AReceiverTheSourceDoesNotTypeIsKeptButMarked()
    {
        var unknown = Assert.Single(P.Handlers("XpoBits"), h => h.Event == ObjectSpaceEvent.Committing);
        Assert.True(unknown.ReceiverUnconfirmed);
        Assert.Equal([$"{Ns}.Order"], unknown.Checks);

        var cast = Assert.Single(P.Handlers("XpoBits"), h => h.Event == ObjectSpaceEvent.ObjectChanged);
        Assert.False(cast.ReceiverUnconfirmed);
        Assert.Equal("Changed", cast.Handler);
        Assert.Equal([$"{Ns}.Invoice"], cast.Checks);
    }

    [Fact]
    public void AnAmbiguousClassNameAttachesNothing() =>
        Assert.Empty(Assert.Single(P.Handlers("NoteController")).Checks);

    [Fact]
    public void ASubscriptionThatNamesNoClassIsStillReported()
    {
        var handler = Assert.Single(P.Handlers("PlainViewController"));
        Assert.Equal(ObjectSpaceEvent.ObjectDeleting, handler.Event);
        Assert.Equal("OnDeleting", handler.Handler);
        Assert.Empty(handler.ControllerTargets);
        Assert.Empty(handler.Checks);
        Assert.Null(handler.TargetOutsideApp);
    }

    [Fact]
    public void APlatformProjectControllerIsRead()
    {
        var handler = Assert.Single(P.Handlers("InvoiceController"));
        Assert.Equal("Shop.Blazor.Server", handler.SourceProject);
        Assert.Equal([$"{Ns}.Invoice"], handler.ControllerTargets);
        Assert.Equal(
            Path.Combine("..", "Shop.Blazor.Server", "Controllers", "InvoiceController.cs"),
            Path.GetRelativePath(SampleProjects.ObjectSpaceHandlerPath, handler.FilePath));
    }

    [Fact]
    public void AnObjectSpaceTheControllerCreatesIsNotItsTarget()
    {
        var popup = Assert.Single(P.Handlers("OrderController"), h => h.Event == ObjectSpaceEvent.ObjectDeleted);
        Assert.True(popup.InController);
        Assert.False(popup.ReceiverUnconfirmed);
        Assert.Empty(popup.ControllerTargets);
    }

    [Fact]
    public void AControllerInANestedNamespaceBlockKeepsItsTarget() =>
        Assert.Equal([$"{Ns}.Vehicle"], Assert.Single(P.Handlers("VehicleController")).ControllerTargets);

    [Theory]
    [InlineData("NoteByGenericController")]
    [InlineData("NoteByTypeofController")]
    public void AFullyQualifiedTargetIsTheClassItNames(string controller) =>
        Assert.Equal([$"{Ns}.B.Note"], Assert.Single(P.Handlers(controller)).ControllerTargets);

    [Fact]
    public void AnInheritedTargetSharedByTwoClassesIsNotGuessed()
    {
        var handler = Assert.Single(P.Handlers("InheritedNoteController"));
        Assert.Empty(handler.ControllerTargets);
        Assert.Null(handler.TargetOutsideApp);
    }

    [Fact]
    public void AnInterfaceTargetIsKeptByName()
    {
        var handler = Assert.Single(P.Handlers("AuditController"));
        Assert.Empty(handler.ControllerTargets);
        Assert.Equal("IAudited", handler.TargetOutsideApp);
    }
}
