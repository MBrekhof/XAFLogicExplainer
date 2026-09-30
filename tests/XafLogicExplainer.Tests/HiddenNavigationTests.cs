using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Tests;

/// <summary>
/// A descendant that redeclares an inherited navigation.
/// </summary>
/// <remarks>
/// Properties folded by name and a redeclared one won, but relationships folded by their own name, and
/// a descendant that hid <c>Student Student</c> behind <c>new string Student</c> has no relationship of
/// that name: the parent's was folded in beside a property list saying <c>string</c>.
/// </remarks>
public class HiddenNavigationTests
{
    [Fact]
    public void AHiddenNavigationIsNoLongerARelationship()
    {
        var archived = SampleProjects.HiddenNavigation.Entity("ArchivedBooking");

        Assert.Equal("string", archived.Property("Student").TypeName);
        Assert.DoesNotContain(archived.Relationships, r => r.PropertyName == "Student");
    }

    [Fact]
    public void AnOverriddenNavigationStaysOne()
    {
        var trial = SampleProjects.HiddenNavigation.Entity("TrialBooking");

        var student = Assert.Single(trial.Relationships, r => r.PropertyName == "Student");
        Assert.Equal("Student", student.RelatedEntity);
    }

    [Fact]
    public void TheParentKeepsItsOwn() =>
        Assert.Single(SampleProjects.HiddenNavigation.Entity("Booking").Relationships, r => r.PropertyName == "Student");
}
