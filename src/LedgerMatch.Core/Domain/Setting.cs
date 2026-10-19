namespace LedgerMatch.Core.Domain;

/// <summary>A named JSON configuration value (e.g. matching parameters).</summary>
public sealed record Setting(long Id, string Key, string ValueJson) : IEntity;
