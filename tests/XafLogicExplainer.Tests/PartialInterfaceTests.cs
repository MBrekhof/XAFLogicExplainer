using XafLogicExplainer.Core.Generators;
using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Tests;

/// <summary>
/// A class split in two, where the second part adds only an interface:
/// <c>partial class Student : BaseObject</c> and <c>partial class Student : ISupportViewLayoutCustomization</c>.
/// </summary>
/// <remarks>
/// Merging the parts took the first one with a base list as the class, on the reasoning that only
/// the hand-written part declares one. Both do here, and <c>Student.Layout.cs</c> sorts before
/// <c>Student.cs</c>, so every class reported the interface as its base and was cited from the layout
/// file. The index then told an agent to derive a new entity from the interface — a class with no key
/// that XAF does not store.
/// </remarks>
public class PartialInterfaceTests
{
    [Fact]
    public void TheBaseClassIsTheOneThePartDeclaringItNames() =>
        Assert.Equal("BaseObject", Entity("Student").BaseType);

    [Fact]
    public void ABaseClassWrittenWithItsNamespaceIsRecognisedToo() =>
        Assert.Equal("DevExpress.Persistent.BaseImpl.EF.BaseObject", Entity("Vehicle").BaseType);

    [Fact]
    public void ABaseTheBusinessClassLibraryShipsIsRecognised() =>
        Assert.Equal("Event", Entity("Shift").BaseType);

    [Fact]
    public void ABaseOfTheApplicationsOwnIsRecognised() =>
        Assert.Equal("AuditedObject", Entity("Lesson").BaseType);

    [Fact]
    public void ItDoesNotDependOnWhichPartSortsFirst() =>
        Assert.Equal("BaseObject", Entity("Instructor").BaseType);

    [Fact]
    public void TheInterfaceIsStillAmongItsBaseTypes() =>
        Assert.Contains("ISupportViewLayoutCustomization", Entity("Student").BaseTypes);

    [Fact]
    public void TheClassIsCitedWhereThePartNamingTheBaseClassDeclaresIt()
    {
        var student = Entity("Student");

        Assert.Equal("Student.cs", Path.GetFileName(student.FilePath));
        Assert.Equal(8, student.Line);
    }

    [Fact]
    public void WhatTheOtherPartDeclaresIsKept()
    {
        var vehicle = Entity("Vehicle");

        Assert.Equal("Fleet", vehicle.NavigationGroup);
        Assert.Equal(["Plate", "Nickname"], vehicle.Properties.Select(property => property.Name));
    }

    [Fact]
    public void TheIndexTellsAnAgentToDeriveFromTheBaseClass()
    {
        var index = new AgentContextGenerator().GenerateIndex(SampleProjects.PartialLayout, []);

        Assert.Contains("- **Entity base class:** `BaseObject`", index);
        Assert.Contains("Derive from `BaseObject`", index);
        Assert.DoesNotContain("`ISupportViewLayoutCustomization`", index);
    }

    private static ExtractedEntity Entity(string className) => SampleProjects.PartialLayout.Entity(className);
}
