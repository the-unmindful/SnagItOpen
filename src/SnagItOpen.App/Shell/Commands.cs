using SnagItOpen.Core.Search;
namespace SnagItOpen.App.Shell;
public sealed record AppCommand(string Id, string Title, string Category, string Icon, string Gesture,
    string Keywords, Func<bool> CanExecute, Action Execute, string? DisabledReason = null);
public sealed class CommandRegistry
{
    private readonly Dictionary<string, AppCommand> _commands = new(StringComparer.Ordinal);
    public IReadOnlyCollection<AppCommand> All => _commands.Values;
    public void Register(AppCommand command) => _commands[command.Id] = command;
    public void Clear() => _commands.Clear();
    public AppCommand? Find(string id) => _commands.GetValueOrDefault(id);
    public IReadOnlyList<AppCommand> Search(string query, IReadOnlyList<string>? recent = null)
    {
        var ranks = (recent ?? []).Distinct().Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        return _commands.Values.Select(c => (Command: c, Score: FuzzyMatcher.Score(query, c.Title, c.Keywords)))
            .Where(x => x.Score >= 0).OrderByDescending(x => x.Score)
            .ThenBy(x => query.Trim().Length == 0 ? ranks.GetValueOrDefault(x.Command.Id, int.MaxValue) : int.MaxValue)
            .ThenBy(x => x.Command.Title, StringComparer.CurrentCultureIgnoreCase).Select(x => x.Command).ToArray();
    }
}
