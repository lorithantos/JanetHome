namespace Janet.Core;

/// <summary>The first operation that did not succeed, and what the service said.</summary>
/// <param name="Item">"container/name" for a blob, "table/partitionKey/rowKey" for an entity.</param>
/// <param name="Operation">deleteBlob, deleteEntity, copyBlob or copyEntity.</param>
public sealed record StorageCopyFailure(string Item, string Operation, AzureError Error);

/// <summary>The exact storage_copy_apply call, in clear mode, that removes what a refresh copied.</summary>
public sealed record StorageCopyClearCall(string Tool, string Spec, string Pair, string Mode);

public sealed record StorageCopyApplyResult
{
    /// <summary>Format version. See contracts\storage-copy-apply.schema.json.</summary>
    public const int ContractVersion = 1;

    public int Contract => ContractVersion;

    /// <summary>applied | cleared | stale | blocked | failed.</summary>
    public required string Outcome { get; init; }

    /// <summary>The hash recomputed now, which differs from the one asked for when the outcome is stale.</summary>
    public required string PlanHash { get; init; }

    public required int DeletedBlobs { get; init; }

    public required int DeletedEntities { get; init; }

    public required int CopiedBlobs { get; init; }

    public required long CopiedBytes { get; init; }

    public required int CopiedEntities { get; init; }

    public required StorageCopyFailure? Failure { get; init; }

    /// <summary>UTC ("o") when the copy phase began; null if it never did.</summary>
    public required string? CopiedAt { get; init; }

    /// <summary>The call that removes a refresh; null in clear mode, and when nothing was written.</summary>
    public required StorageCopyClearCall? Clear { get; init; }
}
