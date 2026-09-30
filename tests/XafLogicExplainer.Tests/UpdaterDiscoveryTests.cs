namespace XafLogicExplainer.Tests;

/// <summary>
/// A module that registers more than one updater.
/// </summary>
/// <remarks>
/// <c>GetModuleUpdaters</c> returns as many updaters as the module likes, and applications split them
/// by concern: one for roles, one for reference data, one per generated feature. The extraction read
/// one — the template's <c>Updater</c> when there was one — so the roles an application actually
/// ships, kept in an updater of their own, were missing from a document that lists seed data as if
/// it were all of it.
/// </remarks>
public class UpdaterDiscoveryTests
{
    [Fact]
    public void SeedDataFromEveryUpdaterIsReadOnceInOrder() =>
        Assert.Equal(
            ["Default", "Room 101", "Night", "Nurse", "Guest", "Admin", "Pharmacist", "Emergency"],
            SampleProjects.Updaters.SeedData.SelectMany(seed => seed.Records).Select(record => record.PropertyValues["Name"]));

    [Fact]
    public void TheTemplateUpdaterComesFirst() =>
        Assert.Equal("PermissionPolicyRole", SampleProjects.Updaters.SeedData[0].EntityType);

    [Fact]
    public void AMethodNamedAlikeInTwoUpdatersIsReadFromBoth() =>
        Assert.Equal(
            ["PermissionPolicyRole", "Ward"],
            SampleProjects.Updaters.SeedData.Where(seed => seed.MethodName == "CreateDefaultRole").Select(seed => seed.EntityType));

    [Fact]
    public void AMigrationInAnUpdaterOtherThanTheTemplatesIsRead()
    {
        var migration = Assert.Single(SampleProjects.Updaters.Migrations);

        Assert.Equal("2.0.0.0", migration.TargetVersion);
        Assert.Contains("DemoteNurses", migration.CallsMethods);
    }

    [Fact]
    public void TwoBlocksDeclaringTheSameVariableSeedTwoDifferentRecords()
    {
        var roles = SampleProjects.Updaters.SeedData
            .Single(seed => seed.MethodName == "UpdateDatabaseAfterUpdateSchema" && seed.EntityType == "PermissionPolicyRole")
            .Records;

        Assert.Equal(["Guest", "Admin"], roles.Select(record => record.PropertyValues["Name"]));
        Assert.False(roles[0].PropertyValues.ContainsKey("IsAdministrative"));
    }

    [Fact]
    public void AnObjectCreatedIntoAVariableDeclaredOutsideTheBlockKeepsItsValues() =>
        Assert.Equal(
            "Pharmacist",
            SampleProjects.Updaters.SeedData.Single(seed => seed.MethodName == "SeedPharmacistRole").Records.Single().PropertyValues["Name"]);

    [Fact]
    public void AnUpdaterTheProjectFileRemovesIsNotRead() =>
        Assert.DoesNotContain(
            SampleProjects.Updaters.SeedData,
            seed => seed.MethodName == "SeedSurgeonRole");

    [Fact]
    public void TwoBlocksCreatingWithNewSeedTwoDifferentRecords()
    {
        // The Session-based `new T(session)` path took the previous record for every creation into a
        // variable, so the blocks below read as one ward carrying the last block's name.
        var records = Assert.Single(SeedOf("""
            if (NeedsNorth)
            {
                var ward = new Ward(Session);
                ward.Name = "North";
            }
            if (NeedsSouth)
            {
                var ward = new Ward(Session);
                ward.Name = "South";
            }
            var east = new Ward(Session) { Name = "East", OpenedOn = new DateTime(2026, 1, 1) };
            east.Code = "E";
            var west = FindWard("West") ?? new Ward(Session);
            west.Name = "West";
            """)).Records;

        // The DateTime inside East's initializer is a value, not a second ward; West is looked up or created.
        Assert.Equal(["North", "South", "East", "West"], records.Select(record => record.PropertyValues["Name"]));
        Assert.Equal("E", records[2].PropertyValues["Code"]);
    }

    /// <summary>Runs the analyzer over a one-off updater whose seeding method has the given body.</summary>
    private static List<Core.Models.ExtractedSeedData> SeedOf(string body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"xaflogic-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "Updater.cs"), $$"""
                using System;
                using DevExpress.ExpressApp.Updating;

                public class Updater : ModuleUpdater
                {
                    public override void UpdateDatabaseAfterUpdateSchema() => SeedWards();

                    private void SeedWards()
                    {
                        {{body}}
                    }
                }
                """);

            return new Core.Analyzers.UpdaterAnalyzer().AnalyzeUpdater(directory, new Core.Models.ExtractionOptions());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
