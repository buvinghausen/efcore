// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace Microsoft.EntityFrameworkCore.Metadata;

public class RelationalForeignKeyOverridesTest
{
    [Fact]
    public void Two_fragments_of_one_entity_share_a_single_model_foreign_key()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.SplitToTable("UserLockout", s => s.Property(u => u.PasswordHash));
            b.SplitToTable("UserProfile", s => s.Property(u => u.DisplayName));
        });

        var model = modelBuilder.FinalizeModel();
        var entityType = model.FindEntityType(typeof(User))!;

        // Spike 2 finding 1: one self-referential FK serves every fragment's linking constraint,
        // which is why override identity must be the (dependent, principal) pair.
        var linkingForeignKeys = entityType.GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType == entityType)
            .ToList();
        Assert.Single(linkingForeignKeys);
    }

    [Fact]
    public void Foreign_key_overrides_are_keyed_by_the_dependent_principal_pair()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.SplitToTable("UserLockout", s => s.Property(u => u.PasswordHash));
            b.SplitToTable("UserProfile", s => s.Property(u => u.DisplayName));
        });

        // EntitySplittingConvention only wires up the shared self-referential linking foreign key as
        // part of model-finalizing conventions, and finalizing the model makes it (and every object
        // reachable from it) read-only — too late to attach overrides. Build the identical linking
        // relationship the convention itself would create (see EntitySplittingConvention.ProcessModelFinalizing),
        // but do it here, before finalization, so the foreign key is still mutable.
        var entityType = (IConventionEntityType)modelBuilder.Model.FindEntityType(typeof(User))!;
        var pk = entityType.FindPrimaryKey()!;
        var foreignKey = (IMutableForeignKey)entityType.Builder
            .HasRelationship(entityType, pk.Properties, pk)!
            .IsUnique(true)!
            .Metadata;

        var users = StoreObjectIdentifier.Table("Users");
        var lockoutPair = new StoreObjectPair(StoreObjectIdentifier.Table("UserLockout"), users);
        var profilePair = new StoreObjectPair(StoreObjectIdentifier.Table("UserProfile"), users);

        RelationalForeignKeyOverrides.GetOrCreate(foreignKey, lockoutPair, ConfigurationSource.Explicit)
            .SetName("fk_user_lockout", ConfigurationSource.Explicit);
        RelationalForeignKeyOverrides.GetOrCreate(foreignKey, profilePair, ConfigurationSource.Explicit)
            .SetName("fk_user_profile", ConfigurationSource.Explicit);

        // One FK object, two distinctly named constraints — impossible with single-store-object identity.
        Assert.Equal("fk_user_lockout", RelationalForeignKeyOverrides.Find(foreignKey, lockoutPair)!.Name);
        Assert.Equal("fk_user_profile", RelationalForeignKeyOverrides.Find(foreignKey, profilePair)!.Name);
    }

    [Fact]
    public void Store_object_pair_equality_is_order_sensitive()
    {
        var a = StoreObjectIdentifier.Table("A");
        var b = StoreObjectIdentifier.Table("B");

        Assert.Equal(new StoreObjectPair(a, b), new StoreObjectPair(a, b));
        Assert.NotEqual(new StoreObjectPair(a, b), new StoreObjectPair(b, a));
        Assert.NotEqual(new StoreObjectPair(a, b).GetHashCode(), new StoreObjectPair(b, a).GetHashCode());
    }

    [Fact]
    public void Foreign_key_name_overrides_resolve_per_pair()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.SplitToTable("UserLockout", s => s.Property(u => u.PasswordHash));
            b.SplitToTable("UserProfile", s => s.Property(u => u.DisplayName));
        });

        // See the comment in Foreign_key_overrides_are_keyed_by_the_dependent_principal_pair: the
        // linking foreign key only exists after finalization, so it must be built manually here to
        // stay mutable while we attach overrides to it.
        //
        // This test deliberately stops short of calling FinalizeModel(): ModelCleanupConvention
        // (src/EFCore/Metadata/Conventions/ModelCleanupConvention.cs, a *Core* finalizing convention
        // that dispatches before any Relational one, including EntitySplittingConvention) removes any
        // foreign key with no navigations before EntitySplittingConvention gets a chance to reuse this
        // one — this manually-built stand-in has none, matching the convention's own construction. The
        // convention then creates a *fresh* linking foreign key from scratch, carrying none of the
        // overrides attached here. Verified empirically: the resulting relational model still used the
        // default-generated name. That is a foreign-key-identity-survival problem (matching Task B5's
        // "Attach, merge, and survival through model rebuilding" scope), not a resolution-logic problem
        // — see Foreign_key_name_override_reaches_the_relational_model below for proof that the
        // resolution seam this task adds does correctly drive RelationalModel construction once the
        // foreign key identity is stable across finalization.
        var entityType = (IConventionEntityType)modelBuilder.Model.FindEntityType(typeof(User))!;
        var pk = entityType.FindPrimaryKey()!;
        var foreignKey = (IMutableForeignKey)entityType.Builder
            .HasRelationship(entityType, pk.Properties, pk)!
            .IsUnique(true)!
            .Metadata;

        var users = StoreObjectIdentifier.Table("Users");
        var lockout = StoreObjectIdentifier.Table("UserLockout");
        var profile = StoreObjectIdentifier.Table("UserProfile");

        RelationalForeignKeyOverrides.GetOrCreate(
            foreignKey, new StoreObjectPair(lockout, users), ConfigurationSource.Explicit)
            .SetName("fk_user_lockout", ConfigurationSource.Explicit);
        RelationalForeignKeyOverrides.GetOrCreate(
            foreignKey, new StoreObjectPair(profile, users), ConfigurationSource.Explicit)
            .SetName("fk_user_profile", ConfigurationSource.Explicit);

        Assert.Equal("fk_user_lockout", foreignKey.GetConstraintName(lockout, users));
        Assert.Equal("fk_user_profile", foreignKey.GetConstraintName(profile, users));
    }

    [Fact]
    public void Foreign_key_name_override_reaches_the_relational_model()
    {
        // Proves Spike 2 finding 2 (RelationalModel builds ForeignKeyConstraint names through
        // GetConstraintName) for a foreign key whose identity is stable across finalization — i.e.
        // one that is not, like the entity-splitting linking FK, itself created as a side effect of
        // model finalizing. See the comment on Foreign_key_name_overrides_resolve_per_pair for why
        // that scenario needs separate, later machinery.
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();
        modelBuilder.Entity<Blog>();
        modelBuilder.Entity<Post>(b => b.HasOne<Blog>().WithMany().HasForeignKey(p => p.BlogId));

        var postType = modelBuilder.Model.FindEntityType(typeof(Post))!;
        var foreignKey = (IMutableForeignKey)postType.GetForeignKeys().Single();
        var blogTable = StoreObjectIdentifier.Table("Blog");
        var postTable = StoreObjectIdentifier.Table("Post");

        RelationalForeignKeyOverrides.GetOrCreate(
            foreignKey, new StoreObjectPair(postTable, blogTable), ConfigurationSource.Explicit)
            .SetName("fk_post_blog", ConfigurationSource.Explicit);

        var relationalModel = modelBuilder.FinalizeModel().GetRelationalModel();
        Assert.Equal(
            "fk_post_blog",
            relationalModel.Tables.Single(t => t.Name == "Post").ForeignKeyConstraints.Single().Name);
    }

    [Fact] // Review Fix 5: the parameterless GetConstraintName() must be override-aware
    public void Parameterless_GetConstraintName_agrees_with_the_store_object_overload()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();
        modelBuilder.Entity<Blog>();
        modelBuilder.Entity<Post>(b => b.HasOne<Blog>().WithMany().HasForeignKey(p => p.BlogId));

        var postType = modelBuilder.Model.FindEntityType(typeof(Post))!;
        var foreignKey = (IMutableForeignKey)postType.GetForeignKeys().Single();
        var blogTable = StoreObjectIdentifier.Table("Blog");
        var postTable = StoreObjectIdentifier.Table("Post");

        // An ordinary, un-split, single-table relationship configured only through the
        // store-object overload -- never through the parameterless HasConstraintName/SetConstraintName.
        foreignKey.SetConstraintName("fk_post_blog_override", postTable, blogTable);

        Assert.Equal(
            foreignKey.GetConstraintName(postTable, blogTable),
            foreignKey.GetConstraintName());
        Assert.Equal("fk_post_blog_override", foreignKey.GetConstraintName());
    }

    [Fact] // Review Fix 1: CanSetConstraintName must compare the stored override, not the resolved name
    public void Explicit_null_foreign_key_constraint_name_override_refuses_a_convention_proposing_the_default_name()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();
        modelBuilder.Entity<Blog>();
        modelBuilder.Entity<Post>(b => b.HasOne<Blog>().WithMany().HasForeignKey(p => p.BlogId));

        var postType = modelBuilder.Model.FindEntityType(typeof(Post))!;
        var foreignKey = (IConventionForeignKey)postType.GetForeignKeys().Single();
        var blogTable = StoreObjectIdentifier.Table("Blog");
        var postTable = StoreObjectIdentifier.Table("Post");

        RelationalForeignKeyOverrides.GetOrCreate(
            (IMutableForeignKey)foreignKey, new StoreObjectPair(postTable, blogTable), ConfigurationSource.Explicit)
            .SetName(null, ConfigurationSource.Explicit);

        var defaultName = foreignKey.GetDefaultName(postTable, blogTable);
        Assert.NotNull(defaultName);

        // Same concern as the key-side Explicit_null_key_name_override_refuses_a_convention_proposing_the_default_name:
        // GetConstraintName(storeObject, principalStoreObject) resolves the explicit-null override
        // to this same default name, so CanSetConstraintName must compare the *stored* override
        // name (null), not that resolved value, or a convention-source write proposing the default
        // name would be wrongly permitted.
        Assert.Null(foreignKey.Builder.HasConstraintName(defaultName, postTable, blogTable, fromDataAnnotation: false));

        var overrides = RelationalForeignKeyOverrides.Find(foreignKey, new StoreObjectPair(postTable, blogTable))!;
        Assert.True(overrides.IsNameOverridden);
        Assert.Null(overrides.Name);
    }

    [Fact]
    public void Explicit_null_foreign_key_constraint_name_override_survives_the_in_memory_runtime_model()
    {
        // Same concern as RelationalKeyOverridesTest's
        // Explicit_null_key_name_override_survives_the_in_memory_runtime_model, for the foreign-key
        // side of B7: RelationalRuntimeModelConvention's conversion of ForeignKeyOverrides into
        // RuntimeRelationalForeignKeyOverrides is a separate code path from both the NativeAOT
        // compiled-model source generator and the design-time resolution logic, and must
        // independently preserve IsNameOverridden: true, Name: null.
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();
        modelBuilder.Entity<Blog>();
        modelBuilder.Entity<Post>(b => b.HasOne<Blog>().WithMany().HasForeignKey(p => p.BlogId));

        var postType = modelBuilder.Model.FindEntityType(typeof(Post))!;
        var foreignKey = (IMutableForeignKey)postType.GetForeignKeys().Single();
        var blogTable = StoreObjectIdentifier.Table("Blog");
        var postTable = StoreObjectIdentifier.Table("Post");

        foreignKey.SetConstraintName("fk_global");
        foreignKey.SetConstraintName(null, postTable, blogTable);

        var modelRuntimeInitializer = FakeRelationalTestHelpers.Instance.CreateContextServices()
            .GetRequiredService<IModelRuntimeInitializer>();
        var runtimeModel = modelRuntimeInitializer.Initialize((IModel)modelBuilder.Model, designTime: false);

        var runtimeForeignKey = runtimeModel.FindEntityType(typeof(Post))!.GetForeignKeys().Single();

        // Proves the annotation was actually *converted* by RelationalRuntimeModelConvention,
        // rather than the design-time RelationalForeignKeyOverrides object merely being carried
        // through untouched (which the covariant IReadOnlyStoreObjectPairDictionary<out T> cast
        // would allow -- reads below would keep working either way, so this type check is the
        // only assertion that actually distinguishes "converted" from "never touched").
        var overrides = runtimeForeignKey.GetOverrides().Single();
        Assert.IsType<RuntimeRelationalForeignKeyOverrides>(overrides);

        // The explicit-null override on the only dependent/principal pair must resolve to the
        // *default* name, not the global "fk_global" annotation -- proving IsNameOverridden: true,
        // Name: null survived the conversion rather than collapsing into "no override at all".
        Assert.Equal(
            runtimeForeignKey.GetDefaultName(postTable, blogTable),
            runtimeForeignKey.GetConstraintName(postTable, blogTable));
    }

    [Fact]
    public void Foreign_key_override_does_not_apply_where_the_constraint_does_not_materialize()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.SplitToTable("UserLockout", s => s.Property(u => u.PasswordHash));
        });

        // See the comment in Foreign_key_name_overrides_resolve_per_pair: the linking foreign key
        // only exists after finalization, so it must be built manually here to stay mutable.
        var entityType = (IConventionEntityType)modelBuilder.Model.FindEntityType(typeof(User))!;
        var pk = entityType.FindPrimaryKey()!;
        var foreignKey = (IMutableForeignKey)entityType.Builder
            .HasRelationship(entityType, pk.Properties, pk)!
            .IsUnique(true)!
            .Metadata;

        var users = StoreObjectIdentifier.Table("Users");
        var absent = StoreObjectIdentifier.Table("NotAMappedTable");

        RelationalForeignKeyOverrides.GetOrCreate(
            foreignKey, new StoreObjectPair(absent, users), ConfigurationSource.Explicit)
            .SetName("fk_nowhere", ConfigurationSource.Explicit);

        // The override is gated on the constraint actually materializing (defaultName != null).
        Assert.Null(foreignKey.GetConstraintName(absent, users));
    }

    [Fact] // Review Fix 4: the linked-foreign-key traversal must consult per-store-object overrides
    public void Foreign_key_override_on_one_fragment_propagates_to_the_linked_foreign_key()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();

        modelBuilder.Entity<SharedCustomer>().ToTable("Customers");
        modelBuilder.Entity<SharedOrder>(b =>
        {
            b.ToTable("Orders");
            b.Property(o => o.CustomerId).HasColumnName("CustomerId");
            b.HasOne<SharedCustomer>().WithMany().HasForeignKey(o => o.CustomerId);
        });
        modelBuilder.Entity<SharedOrderDetails>(b =>
        {
            b.ToTable("Orders");
            // The identifying relationship that makes this table splitting rather than a collision,
            // and what makes SharedOrder's and SharedOrderDetails's foreign keys to SharedCustomer
            // "linked" for GetDefaultName's shared-table traversal.
            b.HasOne<SharedOrder>().WithOne().HasForeignKey<SharedOrderDetails>(d => d.Id);
            // Without an explicit shared column name, SharedTableConvention disambiguates
            // SharedOrderDetails.CustomerId onto its own column, which would make the two foreign
            // keys structurally distinct rather than linked -- defeating the premise of this test.
            b.Property(d => d.CustomerId).HasColumnName("CustomerId");
            b.HasOne<SharedCustomer>().WithMany().HasForeignKey(d => d.CustomerId);
        });

        var orders = StoreObjectIdentifier.Table("Orders");
        var customers = StoreObjectIdentifier.Table("Customers");

        var orderFk = (IMutableForeignKey)modelBuilder.Model.FindEntityType(typeof(SharedOrder))!
            .GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(SharedCustomer));
        var detailsFk = (IMutableForeignKey)modelBuilder.Model.FindEntityType(typeof(SharedOrderDetails))!
            .GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(SharedCustomer));

        // Set the override on only one of the two linked foreign keys.
        orderFk.SetConstraintName("fk_shared", orders, customers);

        // Both must resolve to the same, overridden name -- one database constraint gets one name
        // -- even though only orderFk carries the override directly. Without the fix, detailsFk's
        // traversal only sees the global Name annotation on orderFk (there is none here), falls
        // through to computing its own independent default, and the two resolve differently.
        Assert.Equal("fk_shared", orderFk.GetConstraintName(orders, customers));
        Assert.Equal("fk_shared", detailsFk.GetConstraintName(orders, customers));
    }

    [Fact]
    public void Conflicting_overrides_on_a_deduplicated_constraint_are_rejected()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();

        modelBuilder.Entity<SharedCustomer>().ToTable("Customers");
        modelBuilder.Entity<SharedOrder>(b =>
        {
            b.ToTable("Orders");
            b.Property(o => o.CustomerId).HasColumnName("CustomerId");
            b.HasOne<SharedCustomer>().WithMany().HasForeignKey(o => o.CustomerId);
        });
        modelBuilder.Entity<SharedOrderDetails>(b =>
        {
            b.ToTable("Orders");
            // The identifying relationship that makes this table splitting rather than a collision.
            b.HasOne<SharedOrder>().WithOne().HasForeignKey<SharedOrderDetails>(d => d.Id);
            // Without an explicit shared column name, SharedTableConvention disambiguates
            // SharedOrderDetails.CustomerId onto its own column, which would make the two foreign
            // keys structurally distinct (different columns) rather than the same database
            // constraint -- defeating the premise of this test.
            b.Property(d => d.CustomerId).HasColumnName("CustomerId");
            b.HasOne<SharedCustomer>().WithMany().HasForeignKey(d => d.CustomerId);
        });

        var orders = StoreObjectIdentifier.Table("Orders");
        var customers = StoreObjectIdentifier.Table("Customers");

        // Sanity check the premise: without overrides these two resolve to one constraint name,
        // which is what makes them "the same database constraint".
        var orderFk = (IMutableForeignKey)modelBuilder.Model.FindEntityType(typeof(SharedOrder))!
            .GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(SharedCustomer));
        var detailsFk = (IMutableForeignKey)modelBuilder.Model.FindEntityType(typeof(SharedOrderDetails))!
            .GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(SharedCustomer));

        Assert.Equal(
            orderFk.GetConstraintName(orders, customers),
            detailsFk.GetConstraintName(orders, customers));

        orderFk.SetConstraintName("fk_a", orders, customers);
        detailsFk.SetConstraintName("fk_b", orders, customers);

        var message = Assert.Throws<InvalidOperationException>(() => modelBuilder.FinalizeModel()).Message;
        Assert.Contains("fk_a", message);
        Assert.Contains("fk_b", message);
    }

    [Fact] // Review Fix 2: every candidate per structure must be retained, not just the first
    public void Conflicting_overrides_are_rejected_even_when_the_first_candidate_is_incompatible()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();

        modelBuilder.Entity<SharedCustomer>().ToTable("Customers");
        modelBuilder.Entity<SharedOrder>(b =>
        {
            b.ToTable("Orders");
            b.Property(o => o.CustomerId).HasColumnName("CustomerId");
            // Differs in delete behavior from the other two below -- incompatible with both.
            b.HasOne<SharedCustomer>().WithMany().HasForeignKey(o => o.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SharedOrderDetails>(b =>
        {
            b.ToTable("Orders");
            // The identifying relationship that makes this table splitting rather than a collision.
            b.HasOne<SharedOrder>().WithOne().HasForeignKey<SharedOrderDetails>(d => d.Id);
            b.Property(d => d.CustomerId).HasColumnName("CustomerId");
            b.HasOne<SharedCustomer>().WithMany().HasForeignKey(d => d.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SharedOrderExtra>(b =>
        {
            b.ToTable("Orders");
            // A second, distinct fragment linked to the same root, so three entity types --
            // and three structurally identical foreign keys -- end up sharing "Orders".
            b.HasOne<SharedOrder>().WithOne().HasForeignKey<SharedOrderExtra>(x => x.Id);
            b.Property(x => x.CustomerId).HasColumnName("CustomerId");
            b.HasOne<SharedCustomer>().WithMany().HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        var orders = StoreObjectIdentifier.Table("Orders");
        var customers = StoreObjectIdentifier.Table("Customers");

        var orderFk = (IMutableForeignKey)modelBuilder.Model.FindEntityType(typeof(SharedOrder))!
            .GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(SharedCustomer));
        var detailsFk = (IMutableForeignKey)modelBuilder.Model.FindEntityType(typeof(SharedOrderDetails))!
            .GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(SharedCustomer));
        var extraFk = (IMutableForeignKey)modelBuilder.Model.FindEntityType(typeof(SharedOrderExtra))!
            .GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(SharedCustomer));

        // This test's whole point is the first-recorded candidate being the tricky case: a
        // "compare only against the first recorded candidate" implementation must skip both
        // comparisons to it and so never reach the real conflict between the other two. That only
        // happens if SharedOrder's foreign key is genuinely first in the same enumeration the
        // validator uses. Anchor that explicitly, rather than relying on it implicitly, so a future
        // shift in enumeration order fails this assertion loudly instead of leaving the test green
        // while it silently stops exercising the scenario.
        //
        // RelationalModelValidator enumerates via model.GetEntityTypes().SelectMany(e =>
        // e.GetDeclaredForeignKeys()), and Model orders entity types by full CLR name
        // (SortedDictionary<string, EntityType>, ordinal). "SharedOrder" sorts before
        // "SharedOrderDetails" and "SharedOrderExtra" only because it is a strict prefix of both --
        // an incidental consequence of these classes' names, not a promise the validator makes.
        var orderedCustomerForeignKeys = modelBuilder.Model.GetEntityTypes()
            .Where(e => e.GetTableName() == "Orders")
            .SelectMany(e => e.GetDeclaredForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(SharedCustomer))
            .ToList();
        Assert.Same(orderFk, orderedCustomerForeignKeys[0]);

        // Sanity check the premise: the first-recorded candidate (SharedOrder's foreign key) is
        // genuinely incompatible with the other two, so a "compare only against the first
        // recorded candidate" implementation would skip both comparisons and never reach the real
        // conflict checked below.
        Assert.False(orderFk.AreCompatible(detailsFk, orders, shouldThrow: false));
        Assert.False(orderFk.AreCompatible(extraFk, orders, shouldThrow: false));
        Assert.True(detailsFk.AreCompatible(extraFk, orders, shouldThrow: false));

        detailsFk.SetConstraintName("fk_b", orders, customers);
        extraFk.SetConstraintName("fk_c", orders, customers);

        var message = Assert.Throws<InvalidOperationException>(() => modelBuilder.FinalizeModel()).Message;
        Assert.Contains("fk_b", message);
        Assert.Contains("fk_c", message);
    }

    [Fact] // Review Fix 2: ValidateSharedForeignKeyNameOverrides must not fire when neither FK carries an override
    public void Differing_global_constraint_names_on_a_deduplicated_constraint_are_not_rejected()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();

        modelBuilder.Entity<SharedCustomer>().ToTable("Customers");
        modelBuilder.Entity<SharedOrder>(b =>
        {
            b.ToTable("Orders");
            b.Property(o => o.CustomerId).HasColumnName("CustomerId");
            b.HasOne<SharedCustomer>().WithMany().HasForeignKey(o => o.CustomerId)
                .HasConstraintName("fk_a");
        });
        modelBuilder.Entity<SharedOrderDetails>(b =>
        {
            b.ToTable("Orders");
            // The identifying relationship that makes this table splitting rather than a collision.
            b.HasOne<SharedOrder>().WithOne().HasForeignKey<SharedOrderDetails>(d => d.Id);
            // Without an explicit shared column name, SharedTableConvention disambiguates
            // SharedOrderDetails.CustomerId onto its own column, which would make the two foreign
            // keys structurally distinct (different columns) rather than the same database
            // constraint -- defeating the premise of this test.
            b.Property(d => d.CustomerId).HasColumnName("CustomerId");
            b.HasOne<SharedCustomer>().WithMany().HasForeignKey(d => d.CustomerId)
                .HasConstraintName("fk_b");
        });

        // Two entity types sharing a table, each with its own explicit *global* constraint name and
        // no per-store-object override anywhere: this must keep validating cleanly. Only a genuine
        // per-store-object override conflict -- covered by
        // Conflicting_overrides_on_a_deduplicated_constraint_are_rejected above -- may throw here.
        var model = modelBuilder.FinalizeModel();

        var orders = StoreObjectIdentifier.Table("Orders");
        var customers = StoreObjectIdentifier.Table("Customers");
        var orderFk = model.FindEntityType(typeof(SharedOrder))!
            .GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(SharedCustomer));
        var detailsFk = model.FindEntityType(typeof(SharedOrderDetails))!
            .GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(SharedCustomer));

        Assert.Equal("fk_a", orderFk.GetConstraintName(orders, customers));
        Assert.Equal("fk_b", detailsFk.GetConstraintName(orders, customers));
    }

    [Fact] // Review Fix 3: reattach must merge, not clobber, the reused foreign key's own overrides
    public void Foreign_key_overrides_survive_when_attached_onto_an_existing_foreign_key_with_its_own_overrides()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();

        // Author gets its own foreign key to Blog by convention, and its own override.
        modelBuilder.Entity<Author>(b => b.HasOne<Blog>().WithMany().HasForeignKey("BlogId"));

        var authorFk = (IMutableForeignKey)modelBuilder.Model.FindEntityType(typeof(Author))!.GetForeignKeys().Single();
        var blogTable = StoreObjectIdentifier.Table("Blog");
        var authorTable = StoreObjectIdentifier.Table("Author");
        var fromRootPair = new StoreObjectPair(authorTable, blogTable);

        RelationalForeignKeyOverrides.GetOrCreate(authorFk, fromRootPair, ConfigurationSource.Explicit)
            .SetName("fk_from_root", ConfigurationSource.Explicit);

        // SpecialAuthor starts out standalone with its own structurally-identical foreign key to
        // Blog (same property, same principal), and its own override at a different store object.
        modelBuilder.Entity<SpecialAuthor>();
        modelBuilder.Entity<SpecialAuthor>().HasBaseType((Type?)null);
        modelBuilder.Entity<SpecialAuthor>(b =>
        {
            b.ToTable("Author");
            b.HasOne<Blog>().WithMany().HasForeignKey("BlogId");
        });

        var specialFk = (IMutableForeignKey)modelBuilder.Model.FindEntityType(typeof(SpecialAuthor))!.GetForeignKeys().Single();
        var specialAuthorTable = StoreObjectIdentifier.Table("SpecialAuthor");
        var fromDerivedPair = new StoreObjectPair(specialAuthorTable, blogTable);

        RelationalForeignKeyOverrides.GetOrCreate(specialFk, fromDerivedPair, ConfigurationSource.Explicit)
            .SetName("fk_from_derived", ConfigurationSource.Explicit);

        // Assigning the base type detaches SpecialAuthor's foreign key and attaches it onto
        // Author's existing, structurally-identical foreign key -- the "reused" case.
        modelBuilder.Entity<SpecialAuthor>().HasBaseType<Author>();

        var newFk = modelBuilder.Model.FindEntityType(typeof(Author))!.GetForeignKeys().Single();

        // The guard that makes this test meaningful: the target foreign key really was reused, not
        // recreated, so the merge this test is exercising was actually necessary.
        Assert.Same(authorFk, newFk);

        // Checked against the stored overrides directly, not the resolved constraint name: the
        // "SpecialAuthor" store object used above is a bookkeeping key for the detached override,
        // not Author's real (post-merge) table, so GetConstraintName's materialization gate would
        // fail it for a reason unrelated to what this test is proving.
        Assert.Equal("fk_from_root", RelationalForeignKeyOverrides.Find(newFk, fromRootPair)!.Name);
        Assert.Equal("fk_from_derived", RelationalForeignKeyOverrides.Find(newFk, fromDerivedPair)!.Name);
    }

    [Fact]
    public void Foreign_key_overrides_survive_relationship_re_creation()
    {
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder();
        modelBuilder.Entity<SpecialAuthor>(b => b.ToTable("Author"));
        modelBuilder.Entity<Article>(b => b.HasOne<SpecialAuthor>().WithMany().HasForeignKey(p => p.AuthorId));

        var articleType = modelBuilder.Model.FindEntityType(typeof(Article))!;
        var foreignKey = (IMutableForeignKey)articleType.GetForeignKeys().Single();
        var authorTable = StoreObjectIdentifier.Table("Author");
        var articleTable = StoreObjectIdentifier.Table("Article");
        var pair = new StoreObjectPair(articleTable, authorTable);

        RelationalForeignKeyOverrides.GetOrCreate(foreignKey, pair, ConfigurationSource.Explicit)
            .SetName("fk_article_author", ConfigurationSource.Explicit);

        // Assigning a base type unconditionally detaches every foreign key that references a key
        // declared on the re-parented type and re-creates it against the merged root type:
        // InternalEntityTypeBuilder.HasBaseType (InternalEntityTypeBuilder.cs:1722,
        // RelationshipSnapshot.Attach -> InternalForeignKeyBuilder.Attach).
        modelBuilder.Entity<Author>();
        modelBuilder.Entity<SpecialAuthor>().HasBaseType<Author>();

        var newForeignKey = modelBuilder.Model.FindEntityType(typeof(Article))!.GetForeignKeys().Single();

        // The guard that makes this test meaningful: without it the assertions below could pass
        // simply because nothing was ever detached.
        Assert.NotSame(foreignKey, newForeignKey);
        Assert.False(((IConventionForeignKey)foreignKey).IsInModel);

        Assert.Equal("fk_article_author", newForeignKey.GetConstraintName(articleTable, authorTable));
        Assert.Same(
            newForeignKey,
            ((IConventionRelationalForeignKeyOverrides)RelationalForeignKeyOverrides.Find(newForeignKey, pair)!).ForeignKey);
    }

    [Fact]
    public void Rewriting_constraint_names_per_store_object_produces_no_collisions()
    {
        // This is the acceptance test for the whole workstream -- the shape that
        // EFCore.NamingConventions issue #396 had to work around by *deleting* names.
        //
        // The brief's original version of this test reached for the entity-splitting linking
        // foreign key on a model built with the plain convention builder:
        //
        //     entityType.GetForeignKeys().Single(fk => fk.PrincipalEntityType == entityType)
        //
        // That foreign key does not exist at that point. EntitySplittingConvention.ProcessModelFinalizing
        // is what creates it, as a *model-finalizing* convention. And a hand-built stand-in does not
        // survive finalization: ModelCleanupConvention.RemoveNavigationlessForeignKeys deletes any
        // declared FK with no navigations -- exactly the linking FK's shape -- and it runs first,
        // because ProviderConventionSetBuilder.CreateConventionSet() registers it before
        // RelationalConventionSetBuilder adds EntitySplittingConvention.
        //
        // The path that does work -- and that this test proves -- is a model-finalizing convention
        // registered *after* EntitySplittingConvention: it observes the linking FK the instant that
        // convention creates it, while the model is still mutable. That is exactly how
        // EFCore.NamingConventions itself operates (a plugin convention that runs at model
        // finalization), and is the mechanism RuntimeConventionSetBuilder uses to apply
        // IConventionSetPlugin.ModifyConventions after the provider builder has run.
        var modelBuilder = FakeRelationalTestHelpers.Instance.CreateConventionBuilder(
            configureConventions: b => b.ConventionSet.ModelFinalizingConventions.Add(
                new RewriteConstraintNamesPerStoreObjectConvention()));

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.SplitToTable("user_lockout", s => s.Property(u => u.PasswordHash));
            b.SplitToTable("user_profile", s => s.Property(u => u.DisplayName));
        });

        var relationalModel = modelBuilder.FinalizeModel().GetRelationalModel();

        var constraintNames = relationalModel.Tables
            .SelectMany(t => t.UniqueConstraints.Select(c => c.Name)
                .Concat(t.ForeignKeyConstraints.Select(c => c.Name)))
            .ToList();

        // The #396 symptom was two identically named constraints; there must be none now.
        Assert.Equal(constraintNames.Count, constraintNames.Distinct().Count());
        Assert.Contains("pk_user_lockout", constraintNames);
        Assert.Contains("fk_user_profile_users_id", constraintNames);
    }

    /// <summary>
    ///     Stands in for a naming-convention plugin such as EFCore.NamingConventions: it rewrites
    ///     every key and the entity-splitting linking foreign key with per-store-object names, run
    ///     as a model-finalizing convention registered after <see cref="EntitySplittingConvention" />
    ///     so the linking foreign key already exists and the model is still mutable.
    /// </summary>
    private sealed class RewriteConstraintNamesPerStoreObjectConvention : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(
            IConventionModelBuilder modelBuilder,
            IConventionContext<IConventionModelBuilder> context)
        {
            var entityType = modelBuilder.Metadata.FindEntityType(typeof(User))!;
            var key = (IMutableKey)entityType.FindPrimaryKey()!;
            var linkingForeignKey = (IMutableForeignKey)entityType.GetForeignKeys()
                .Single(fk => fk.PrincipalEntityType == entityType);

            var users = StoreObjectIdentifier.Table("users");
            var lockout = StoreObjectIdentifier.Table("user_lockout");
            var profile = StoreObjectIdentifier.Table("user_profile");

            // What a naming convention does: rewrite per store object instead of writing one global name.
            foreach (var table in new[] { users, lockout, profile })
            {
                key.SetName("pk_" + table.Name, table);
            }

            linkingForeignKey.SetConstraintName("fk_user_lockout_users_id", lockout, users);
            linkingForeignKey.SetConstraintName("fk_user_profile_users_id", profile, users);
        }
    }

    private class Blog
    {
        public int Id { get; set; }
    }

    private class Post
    {
        public int Id { get; set; }
        public int BlogId { get; set; }
    }

    private class Author
    {
        public int Id { get; set; }
    }

    private class SpecialAuthor : Author
    {
        public string Tier { get; set; } = null!;
    }

    private class Article
    {
        public int Id { get; set; }
        public int AuthorId { get; set; }
    }

    private class User
    {
        public int Id { get; set; }

        // Split-table tests move PasswordHash and DisplayName to secondary tables; this stays on
        // the main table so the main store object keeps at least one non-key property, as required
        // by validation.
        public string Email { get; set; } = null!;

        public string PasswordHash { get; set; } = null!;
        public string DisplayName { get; set; } = null!;
    }

    private class SharedCustomer
    {
        public int Id { get; set; }
    }

    private class SharedOrder
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
    }

    private class SharedOrderDetails
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
    }

    private class SharedOrderExtra
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
    }
}
