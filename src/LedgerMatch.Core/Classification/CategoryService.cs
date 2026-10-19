using LedgerMatch.Core.Domain;
using LedgerMatch.Core.History;
using LedgerMatch.Core.Persistence;

namespace LedgerMatch.Core.Classification;

public sealed class CategoryService(ILedgerStore store, ILedgerQueries queries, UndoRedoService history)
{
    public Category Add(string name)
    {
        name = ValidateName(name, null);
        var category = new Category(store.ReserveIds(1), name, false);
        history.Execute(new ChangeSetBuilder($"新增科目「{name}」").Insert(category).Build());
        return category;
    }

    public Category Rename(long id, string name)
    {
        var existing = queries.GetCategory(id) ?? throw new LedgerException($"找不到科目 #{id}。");
        name = ValidateName(name, id);
        var renamed = existing with { Name = name };
        history.Execute(new ChangeSetBuilder($"科目改名「{existing.Name}」→「{name}」").Update(existing, renamed).Build());
        return renamed;
    }

    public void Delete(long id)
    {
        var existing = queries.GetCategory(id) ?? throw new LedgerException($"找不到科目 #{id}。");
        if (existing.IsSystem)
        {
            throw new LedgerException($"「{existing.Name}」是系統科目，不能刪除。");
        }

        if (queries.IsCategoryInUse(id))
        {
            throw new LedgerException($"科目「{existing.Name}」仍被交易、拆分或規則使用，不能刪除。");
        }

        history.Execute(new ChangeSetBuilder($"刪除科目「{existing.Name}」").Delete(existing).Build());
    }

    private string ValidateName(string name, long? selfId)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new LedgerException("科目名稱不可空白。");
        }

        if (queries.GetCategories().Any(c => c.Id != selfId && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LedgerException($"已有名為「{name}」的科目。");
        }

        return name;
    }
}
