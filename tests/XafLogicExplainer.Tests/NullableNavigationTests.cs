using XafLogicExplainer.Core.Generators;
using XafLogicExplainer.Core.Models;

namespace XafLogicExplainer.Tests;

/// <summary>
/// A to-one navigation written with a nullable annotation: <c>public virtual Student? Student</c>.
/// </summary>
/// <remarks>
/// With nullable reference types on, every reference that may be empty is written that way, which
/// in a generated application is every to-one navigation. The type was matched against the class
/// names as written, question mark included, so none of them was a relationship: the index showed
/// only the collection side, and "what does a lesson belong to?" had no answer. A navigation written
/// without the annotation in the same class was read, which is what made the gap look deliberate.
/// </remarks>
public class NullableNavigationTests
{
    [Fact]
    public void TheLessonsApplicationIsReadAsEfCore() =>
        Assert.Contains("EF", Lessons.OrmType, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ANullableReferenceIsARelationship()
    {
        var student = Assert.Single(Lesson.Relationships, r => r.PropertyName == "Student");

        Assert.Equal("Student", student.RelatedEntity);
        Assert.Equal(RelationshipType.ManyToOne, student.Type);
    }

    [Fact]
    public void ItReadsLikeTheReferenceWrittenWithoutTheAnnotation()
    {
        var instructor = Assert.Single(Lesson.Relationships, r => r.PropertyName == "Instructor");

        Assert.Equal("Instructor", instructor.RelatedEntity);
        Assert.Equal(RelationshipType.ManyToOne, instructor.Type);
    }

    [Fact]
    public void ThePropertyKeepsItsTypeAsWritten() =>
        Assert.Equal("Student?", Lesson.Property("Student").TypeName);

    [Fact]
    public void TheIndexSaysWhichStudentTheLessonBelongsTo()
    {
        var index = new AgentContextGenerator().GenerateIndex(Lessons, []);

        Assert.Contains("one `Student`", index);
    }

    [Fact]
    public void ANullableAssociationNamesTheClassItPointsAt()
    {
        var customer = Assert.Single(
            SampleProjects.NullableNavigationXpo.Entity("Rental").Relationships,
            r => r.PropertyName == "Customer");

        Assert.Equal("Customer", customer.RelatedEntity);
        Assert.Equal("Customer-Rentals", customer.AssociationName);
        Assert.Equal(RelationshipType.ManyToOne, customer.Type);
    }

    private static ExtractedProject Lessons => SampleProjects.NullableNavigationEf;

    private static ExtractedEntity Lesson => Lessons.Entity("Lesson");
}
