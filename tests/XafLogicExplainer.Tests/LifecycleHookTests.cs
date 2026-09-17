using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Tests;

/// <summary>
/// Code a business class runs when the Object Space creates, loads or saves one of its objects.
/// </summary>
/// <remarks>
/// XAF documents the business class as the place for that logic: <c>OnCreated</c>, <c>OnLoaded</c> and
/// <c>OnSaving</c> in EF Core, <c>AfterConstruction</c> and <c>OnSaving</c> in XPO. None of it was read, so an
/// application whose every class stamps who changed it on save was described without a word of it, in an
/// index that calls its inventories complete.
/// </remarks>
public class LifecycleHookTests
{
    [Fact]
    public void AnOverrideOfOnSavingIsAHook()
    {
        var hook = Assert.Single(Entity("AuditedObject").Lifecycle);

        Assert.Equal(LifecycleTrigger.Saving, hook.Trigger);
        Assert.Equal("OnSaving", hook.MethodName);
        Assert.Equal("AuditedObject.cs", Path.GetFileName(hook.FilePath));
        Assert.Equal(12, hook.Line);
        Assert.Equal(["ChangedOn", "ChangedBy"], hook.AssignedProperties);
    }

    [Fact]
    public void AClassThatDeclaresNoHookInheritsItsBasesHook()
    {
        var hook = Assert.Single(Entity("Car").Lifecycle);

        Assert.Equal("AuditedObject", hook.InheritedFrom);
        Assert.Equal(["ChangedOn", "ChangedBy"], hook.AssignedProperties);
    }

    [Fact]
    public void AnOverrideThatDoesNotCallBaseReplacesTheHookAboveIt()
    {
        var hook = Assert.Single(Entity("Truck").Lifecycle);

        Assert.Null(hook.InheritedFrom);
        Assert.False(hook.CallsBase);
        Assert.Equal(["Axles", "Cab", "Trips"], hook.AssignedProperties);
    }

    [Fact]
    public void ABaseCallToAHelperOfTheSameNameDoesNotKeepTheHookAboveIt()
    {
        var hook = Assert.Single(Entity("Trailer").Lifecycle);

        Assert.False(hook.CallsBase);
        Assert.Equal(["Hitches"], hook.AssignedProperties);
    }

    [Fact]
    public void AnAssignmentThroughThisIsThePropertyEvenBesideALocalOfItsName() =>
        Assert.Contains("Axles", Entity("Truck").Lifecycle.Single().AssignedProperties);

    [Fact]
    public void ALambdasParameterDoesNotHideThePropertyOutsideTheLambda() =>
        Assert.Contains("Cab", Entity("Truck").Lifecycle.Single().AssignedProperties);

    [Fact]
    public void ALocalDeclaredInOneSwitchSectionIsTheNameInTheOthers() =>
        Assert.DoesNotContain("Gear", Entity("Truck").Lifecycle.Single().AssignedProperties);

    [Fact]
    public void AnIncrementIsAnAssignment() =>
        Assert.Contains("Trips", Entity("Truck").Lifecycle.Single().AssignedProperties);

    [Fact]
    public void AnOverrideOfANewVirtualNamesakeIsNotAHookAndKeepsTheHookAboveIt()
    {
        var hook = Assert.Single(Entity("Ledger").Lifecycle);

        Assert.Equal("AuditedObject", hook.InheritedFrom);
        Assert.Equal("AuditedObject", Assert.Single(Entity("LedgerBase").Lifecycle).InheritedFrom);
    }

    [Fact]
    public void ANewVirtualNamesakeOnAClassThatNamesTheInterfaceAgainIsTheHookAndSoIsItsOverride()
    {
        var hook = Assert.Single(Entity("Journal").Lifecycle);

        Assert.Null(hook.InheritedFrom);
        Assert.Equal(["Lines"], hook.AssignedProperties);
        Assert.Equal(["Entries"], Assert.Single(Entity("JournalBase").Lifecycle).AssignedProperties);
    }

    [Fact]
    public void AnOverrideOfANewVirtualNamesakeOnAClassThatNamesTheInterfaceAgainIsTheHook() =>
        Assert.Equal(["Pages"], Assert.Single(Entity("Diary").Lifecycle).AssignedProperties);

    [Fact]
    public void AnOverrideBelowAnExplicitImplementationIsNotAHookAndKeepsIt()
    {
        var hook = Assert.Single(Entity("PartChild").Lifecycle);

        Assert.Equal("Part", hook.InheritedFrom);
        Assert.Equal(["Stock"], hook.AssignedProperties);
    }

    [Fact]
    public void ABaseCallInsideALambdaDoesNotKeepTheHookAboveIt()
    {
        var hook = Assert.Single(Entity("Bus").Lifecycle);

        Assert.False(hook.CallsBase);
        Assert.Equal(["Doors"], hook.AssignedProperties);
    }

    [Fact]
    public void AnOverrideThatCallsBaseRunsBoth() =>
        Assert.Equal(
            [(null, "Seats"), ("AuditedObject", "ChangedOn")],
            Entity("Van").Lifecycle.Select(hook => (hook.InheritedFrom, hook.AssignedProperties[0])));

    [Fact]
    public void AFieldTheHookAssignsIsNotAProperty() =>
        Assert.Equal(["Seats"], Entity("Van").Lifecycle.Single(hook => hook.InheritedFrom is null).AssignedProperties);

    [Fact]
    public void CreateAndLoadHooksAreRead() =>
        Assert.Equal(
            [LifecycleTrigger.Created, LifecycleTrigger.Loaded],
            Entity("Vehicle").Lifecycle.Select(hook => hook.Trigger));

    [Fact]
    public void ALocalALambdaAnInitializerAndAnotherObjectsSetterAreNotAssignmentsToTheObject() =>
        Assert.Equal(["Status"], Entity("Vehicle").Lifecycle.Single(hook => hook.Trigger == LifecycleTrigger.Created).AssignedProperties);

    [Fact]
    public void AnExplicitImplementationOfTheInterfaceIsAHookAndAPublicNamesakeBesideItIsNot() =>
        Assert.Equal(["Stock"], Assert.Single(Entity("Part").Lifecycle).AssignedProperties);

    [Fact]
    public void PublicMethodsAreHooksOnAClassThatImplementsTheInterface() =>
        Assert.Equal(
            [LifecycleTrigger.Created, LifecycleTrigger.Loaded, LifecycleTrigger.Saving],
            Entity("Tyre").Lifecycle.Select(hook => hook.Trigger));

    [Fact]
    public void AnOverloadAGenericMethodAndXposCreateHookAreNotTheInterfacesMethods()
    {
        var hook = Assert.Single(Entity("Tyre").Lifecycle, hook => hook.Trigger == LifecycleTrigger.Saving);

        Assert.Equal(18, hook.Line);
        Assert.Equal("OnCreated", Entity("Tyre").Lifecycle.Single(hook => hook.Trigger == LifecycleTrigger.Created).MethodName);
    }

    [Fact]
    public void APublicMethodThatOnlySharesTheNameIsNotAHook() =>
        Assert.Empty(Entity("Wheel").Lifecycle);

    [Fact]
    public void AHookOnTheOtherPartOfAPartialClassBelongsToTheClass()
    {
        var hook = Assert.Single(Entity("Invoice").Lifecycle);

        Assert.Equal("Invoice.Hooks.cs", Path.GetFileName(hook.FilePath));
        Assert.Equal(["Total"], hook.AssignedProperties);
    }

    [Fact]
    public void AHookOnABaseInAReferencedLibraryReachesTheClassesDerivingFromIt()
    {
        var hook = Assert.Single(Entity("Customer").Lifecycle);

        Assert.Equal("TrackedRecord", hook.InheritedFrom);
        Assert.Equal(["TrackedOn"], hook.AssignedProperties);
        Assert.DoesNotContain(SampleProjects.Lifecycle.Entities, entity => entity.Namespace == "Tracking.Core");
    }

    [Fact]
    public void XposAfterConstructionIsACreateHook()
    {
        var hook = Assert.Single(SampleProjects.LifecycleXpo.Entity("Crate").Lifecycle);

        Assert.Equal(LifecycleTrigger.Created, hook.Trigger);
        Assert.Equal("AfterConstruction", hook.MethodName);
        Assert.Equal(["Label"], hook.AssignedProperties);
    }

    private static ExtractedEntity Entity(string className) => SampleProjects.Lifecycle.Entity(className);
}
