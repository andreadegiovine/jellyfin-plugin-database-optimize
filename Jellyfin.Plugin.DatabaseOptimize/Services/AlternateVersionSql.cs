namespace Jellyfin.Plugin.DatabaseOptimize.Services;

/// <summary>
/// SQL used to check and repair the alternate versions of a video. Jellyfin models them with
/// <c>BaseItems.OwnerId</c> and <c>BaseItems.PrimaryVersionId</c> (both pointing to the primary
/// item) plus a <c>LinkedChildren</c> row of type 2 (LocalAlternateVersion) from the primary item
/// to every alternate. Extras (trailers, special features...) also use OwnerId, but have an
/// ExtraType, so they are always excluded.
/// </summary>
internal static class AlternateVersionSql
{
    /// <summary>
    /// The value of LinkedChildType.LocalAlternateVersion.
    /// </summary>
    public const int LocalAlternateVersion = 2;

    /// <summary>
    /// Counts alternates owned by another alternate.
    /// </summary>
    public const string CountOwnerChains =
        "SELECT count(*) FROM BaseItems a JOIN BaseItems b ON b.Id = a.OwnerId " +
        "WHERE a.OwnerId IS NOT NULL AND a.ExtraType IS NULL AND b.OwnerId IS NOT NULL AND b.ExtraType IS NULL";

    /// <summary>
    /// Counts alternates whose PrimaryVersionId differs from OwnerId.
    /// </summary>
    public const string CountPrimaryMismatches =
        "SELECT count(*) FROM BaseItems WHERE OwnerId IS NOT NULL AND ExtraType IS NULL " +
        "AND (PrimaryVersionId IS NULL OR PrimaryVersionId <> OwnerId)";

    /// <summary>
    /// Counts alternate version links that do not match BaseItems.OwnerId.
    /// </summary>
    public const string CountInconsistentLinks =
        "SELECT count(*) FROM LinkedChildren WHERE ChildType = 2 AND NOT EXISTS " +
        "(SELECT 1 FROM BaseItems c WHERE c.Id = LinkedChildren.ChildId AND c.OwnerId = LinkedChildren.ParentId)";

    /// <summary>
    /// Counts alternates without their link.
    /// </summary>
    public const string CountMissingLinks =
        "SELECT count(*) FROM BaseItems a WHERE a.OwnerId IS NOT NULL AND a.ExtraType IS NULL AND NOT EXISTS " +
        "(SELECT 1 FROM LinkedChildren l WHERE l.ParentId = a.OwnerId AND l.ChildId = a.Id AND l.ChildType = 2)";

    /// <summary>
    /// Lists the alternates with a file path, to check that the file still exists.
    /// </summary>
    public const string ListAlternatePaths =
        "SELECT Id, Path FROM BaseItems WHERE OwnerId IS NOT NULL AND ExtraType IS NULL AND Path IS NOT NULL";

    /// <summary>
    /// When PrimaryVersionId points to a real primary item, the alternate belongs to it.
    /// </summary>
    public const string OwnerFromPrimaryVersion =
        "UPDATE BaseItems SET OwnerId = PrimaryVersionId WHERE OwnerId IS NOT NULL AND ExtraType IS NULL " +
        "AND PrimaryVersionId IS NOT NULL AND PrimaryVersionId <> OwnerId AND EXISTS " +
        "(SELECT 1 FROM BaseItems p WHERE p.Id = BaseItems.PrimaryVersionId AND p.OwnerId IS NULL)";

    /// <summary>
    /// Moves an alternate owned by another alternate one level up. Run until it changes nothing.
    /// </summary>
    public const string FlattenOwnerChain =
        "UPDATE BaseItems SET OwnerId = (SELECT b.OwnerId FROM BaseItems b WHERE b.Id = BaseItems.OwnerId) " +
        "WHERE OwnerId IS NOT NULL AND ExtraType IS NULL AND EXISTS " +
        "(SELECT 1 FROM BaseItems b WHERE b.Id = BaseItems.OwnerId AND b.OwnerId IS NOT NULL AND b.ExtraType IS NULL)";

    /// <summary>
    /// Aligns PrimaryVersionId with OwnerId.
    /// </summary>
    public const string AlignPrimaryVersion =
        "UPDATE BaseItems SET PrimaryVersionId = OwnerId WHERE OwnerId IS NOT NULL AND ExtraType IS NULL " +
        "AND (PrimaryVersionId IS NULL OR PrimaryVersionId <> OwnerId)";

    /// <summary>
    /// Removes the alternate version links that do not match BaseItems.OwnerId.
    /// </summary>
    public const string DeleteInconsistentLinks =
        "DELETE FROM LinkedChildren WHERE ChildType = 2 AND NOT EXISTS " +
        "(SELECT 1 FROM BaseItems c WHERE c.Id = LinkedChildren.ChildId AND c.OwnerId = LinkedChildren.ParentId)";

    /// <summary>
    /// Adds the missing alternate version links after the existing ones of the same primary item.
    /// </summary>
    public const string InsertMissingLinks =
        "INSERT OR IGNORE INTO LinkedChildren (ParentId, SortOrder, ChildId, ChildType) " +
        "SELECT a.OwnerId, " +
        "COALESCE((SELECT max(l2.SortOrder) FROM LinkedChildren l2 WHERE l2.ParentId = a.OwnerId), -1) " +
        "+ ROW_NUMBER() OVER (PARTITION BY a.OwnerId ORDER BY a.Path), a.Id, 2 " +
        "FROM BaseItems a WHERE a.OwnerId IS NOT NULL AND a.ExtraType IS NULL AND NOT EXISTS " +
        "(SELECT 1 FROM LinkedChildren l WHERE l.ParentId = a.OwnerId AND l.ChildId = a.Id AND l.ChildType = 2)";
}
