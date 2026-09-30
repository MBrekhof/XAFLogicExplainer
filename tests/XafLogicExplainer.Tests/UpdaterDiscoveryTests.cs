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

    [Fact]
    public void AHelperTakingItsValuesAsArgumentsSeedsOneRecordPerCall()
    {
        // The role names exist only at the call sites; the helper's own body says `role.Name = name`.
        var seed = Assert.Single(Extract("""
            public override void UpdateDatabaseAfterUpdateSchema()
            {
                SeedRole("Administrators", role => role.IsAdministrative = true);
                SeedRole("SuperUser", role => { });
                SeedRole(NameFromSettings(), role => { });
            }

            private void SeedRole(string name, Action<PermissionPolicyRole> configure)
            {
                var existing = ObjectSpace.FindObject<PermissionPolicyRole>(new BinaryOperator("Name", name));
                if (existing != null) return;

                var role = ObjectSpace.CreateObject<PermissionPolicyRole>();
                role.Name = name;
                role.PermissionPolicy = SecurityPermissionPolicy.DenyAllByDefault;
                configure(role);
            }
            """).SeedData);

        Assert.Equal("SeedRole", seed.MethodName);
        Assert.Equal("PermissionPolicyRole", seed.EntityType);
        Assert.Equal(["Administrators", "SuperUser", "set by the caller"], seed.Records.Select(record => record.PropertyValues["Name"]));
        Assert.All(seed.Records, record => Assert.Equal("SecurityPermissionPolicy.DenyAllByDefault", record.PropertyValues["PermissionPolicy"]));
    }

    [Fact]
    public void AnOverloadedHelperSeedsEachCallFromTheOverloadItBindsTo()
    {
        var records = Extract("""
            public override void UpdateDatabaseAfterUpdateSchema()
            {
                SeedRole("Guest");
                SeedRole("Admin", true);
                SeedRole(administrative: false, name: "Clerk");
            }

            private void SeedRole(string name)
            {
                var role = ObjectSpace.CreateObject<PermissionPolicyRole>();
                role.Name = name;
            }

            private void SeedRole(string name, bool administrative)
            {
                var role = ObjectSpace.CreateObject<PermissionPolicyRole>();
                role.Name = name;
                role.IsAdministrative = administrative;
            }
            """).SeedData.SelectMany(seed => seed.Records).ToList();

        Assert.Equal(["Guest", "Admin", "Clerk"], records.Select(record => record.PropertyValues["Name"]));
        Assert.False(records[0].PropertyValues.ContainsKey("IsAdministrative"));
        Assert.Equal("true", records[1].PropertyValues["IsAdministrative"]);
        Assert.Equal("false", records[2].PropertyValues["IsAdministrative"]);
    }

    [Fact]
    public void AnObjectBuiltOnlyToLookSomethingUpIsNotWhatIsSeeded()
    {
        var seed = Assert.Single(Extract("""
            public override void UpdateDatabaseAfterUpdateSchema() => SeedWards();

            private void SeedWards()
            {
                var ward = new Ward(Session);
                ward.Name = "North";
                var existing = Session.FindObject<Ward>(new BinaryOperator("Name", "North"));
            }
            """).SeedData);

        Assert.Equal("Ward", seed.EntityType);
        Assert.Equal("North", Assert.Single(seed.Records).PropertyValues["Name"]);
    }

    [Fact]
    public void UsersCreatedThroughTheUserManagerAreSeeds()
    {
        var seeds = Extract("""
            public override void UpdateDatabaseAfterUpdateSchema()
            {
                SeedServiceUser();
                EnsureTestUser(userManager, "Tester", "", "Default");
            }

            private void SeedServiceUser()
            {
                var role = ObjectSpace.FindObject<PermissionPolicyRole>(new BinaryOperator("Name", "Default"));
                _ = userManager.CreateUser<global::Clinic.Module.BusinessObjects.ApplicationUser>(ObjectSpace, "HangfireJob", "", user => user.Roles.Add(role));
                _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, "Admin", customizeUser: user => user.Roles.Add(role), password: "");
                _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, principal);
                _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, principal, null);
                _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, currentPrincipal, user => user.Roles.Add(role));
                _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, currentPrincipal, AddDefaultRole);
                _ = userManager.CreateUser<ApplicationUser>(objectSpace: ObjectSpace, principal: currentPrincipal);
                _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, currentPrincipal, this.AddDefaultRole);
                _ = userManager.CreateUser<ApplicationUser>(objectSpace: ObjectSpace, "Clerk", "", user => { });
            }

            private void AddDefaultRole(ApplicationUser user) { }

            private void EnsureTestUser(UserManager userManager, string userName, string password, string roleName)
            {
                if (userManager.FindUserByName<ApplicationUser>(ObjectSpace, userName) != null) return;
                _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, userName, password, user => { });
            }
            """).SeedData;

        Assert.All(seeds, seed => Assert.Equal("ApplicationUser", seed.EntityType));
        Assert.Equal(
            ["HangfireJob", "Admin", "Clerk", "Tester"],
            seeds.SelectMany(seed => seed.Records).Select(record => record.PropertyValues["UserName"]));
    }

    [Fact]
    public void WhatTheUpdateMethodCreatesItselfIsASeedPerClassWithoutACallToTheBase()
    {
        var seeds = Extract("""
            public override void UpdateDatabaseAfterUpdateSchema()
            {
                var guest = ObjectSpace.CreateObject<PermissionPolicyRole>();
                guest.Name = "Guest";
                var ward = ObjectSpace.CreateObject<Ward>();
                ward.Name = "North";
                var admin = ObjectSpace.CreateObject<PermissionPolicyRole>();
                admin.Name = "Admin";
            }
            """).SeedData;

        Assert.Equal(["PermissionPolicyRole", "Ward"], seeds.Select(seed => seed.EntityType));
        Assert.Equal("Seed data: Permission Policy Role", seeds[0].Description);
        Assert.Equal(["Guest", "Admin"], seeds[0].Records.Select(record => record.PropertyValues["Name"]));
        Assert.All(seeds, seed => Assert.Equal("Updater", seed.UpdaterClass));
    }

    [Fact]
    public void ASeedTheUpdateMethodCreatesItselfIsNamedOnThePageAfterWhatItCreates()
    {
        var page = new Core.Generators.HtmlExplainerGenerator().Generate(Extract("""
            public override void UpdateDatabaseAfterUpdateSchema()
            {
                var ward = ObjectSpace.CreateObject<Ward>();
                ward.Name = "North";
            }
            """));

        Assert.Contains("<span class=\"card__name\">Ward</span>", page);
        Assert.Contains("in Updater.UpdateDatabaseAfterUpdateSchema", page);
    }

    /// <summary>
    /// Extracts a one-off module holding a <c>Ward</c> business class and an updater with the given
    /// members, through the whole extractor so the analyzer is given the module's business classes.
    /// </summary>
    private static Core.Models.ExtractedProject Extract(string updaterMembers)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"xaflogic-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "BusinessObjects"));

        try
        {
            File.WriteAllText(Path.Combine(directory, "BusinessObjects", "Ward.cs"), """
                using DevExpress.Persistent.Base;
                using DevExpress.Persistent.BaseImpl;

                [DefaultClassOptions]
                public class Ward : BaseObject
                {
                    public Ward(Session session) : base(session) { }
                    public string Name { get; set; }
                }
                """);

            File.WriteAllText(Path.Combine(directory, "Updater.cs"), $$"""
                using System;
                using DevExpress.Data.Filtering;
                using DevExpress.ExpressApp.Updating;

                public class Updater : ModuleUpdater
                {
                    {{updaterMembers}}
                }
                """);

            return new Core.Analyzers.LogicExtractor().ExtractFromSourceDirectory(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
