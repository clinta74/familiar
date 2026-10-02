namespace Familiar.Domain.Familiars;

/// <summary>
/// A VRM model a user uploaded for their familiar (PLAN.md §5.1, §5.5). Kept in the database, so
/// the nightly backup has it and there's no volume to manage.
/// </summary>
public sealed class AvatarAsset
{
    /// <summary>Generous for VRoid exports (typically 10-30 MB) without inviting whole scenes.</summary>
    public const int MaxBytes = 40 * 1024 * 1024;

    /// <summary>Per account: enough to try a few, not a model library.</summary>
    public const int MaxPerAccount = 5;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid AccountId { get; init; }

    /// <summary>The model's own title, or the file name.</summary>
    public required string Name { get; set; }

    public required byte[] Content { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Hex SHA-256 of the content: the ETag, and how a re-upload is spotted.</summary>
    public required string Sha256 { get; init; }

    /// <summary>What the model says about itself - version, authors, licence - as JSON, for display.</summary>
    public required string Meta { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
