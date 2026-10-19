using System.Text.Json;
using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Matching;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Setup;

public sealed class SettingsService(ILedgerStore store, ILedgerQueries queries, UndoRedoService history)
{
    public const string MatchingKey = "matching";

    public MatchingSettings GetMatchingSettings() =>
        queries.GetSetting(MatchingKey) is { } s
            ? JsonSerializer.Deserialize<MatchingSettings>(s.ValueJson, LedgerJson.Options)!
            : MatchingSettings.Default;

    public void SaveMatchingSettings(MatchingSettings settings)
    {
        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            throw new LedgerException(string.Join(Environment.NewLine, problems));
        }

        var json = LedgerJson.Serialize(settings);
        var changes = new ChangeSetBuilder("修改配對參數");
        if (queries.GetSetting(MatchingKey) is { } existing)
        {
            changes.Update(existing, existing with { ValueJson = json });
        }
        else
        {
            changes.Insert(new Setting(store.ReserveIds(1), MatchingKey, json));
        }

        history.Execute(changes.Build());
    }
}
