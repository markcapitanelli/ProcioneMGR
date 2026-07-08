namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Pure DAG validation of a stage list: every enabled stage's dependency groups must be
/// satisfied by at least one enabled stage ordered BEFORE it. Static and side-effect free so
/// it is directly unit-testable (the engine and the UI both delegate here).
/// </summary>
public static class PipelineDagValidator
{
    /// <param name="stages">The configured stages (enabled + disabled).</param>
    /// <param name="dependenciesByName">Stage name → dependency groups (from the prototypes).</param>
    /// <param name="displayNames">Stage name → display name (for readable messages).</param>
    public static List<string> Validate(
        IReadOnlyList<StageConfig> stages,
        IReadOnlyDictionary<string, IReadOnlyList<StageDependency>> dependenciesByName,
        IReadOnlyDictionary<string, string> displayNames)
    {
        var problems = new List<string>();
        var enabled = stages.Where(s => s.Enabled).OrderBy(s => s.Order).ToList();

        foreach (var stage in stages)
        {
            if (!dependenciesByName.ContainsKey(stage.Type))
            {
                problems.Add($"Stage sconosciuto: '{stage.Type}'.");
            }
        }
        if (enabled.Count == 0)
        {
            problems.Add("Nessuno stage abilitato.");
            return problems;
        }

        foreach (var stage in enabled)
        {
            if (!dependenciesByName.TryGetValue(stage.Type, out var dependencies)) continue;
            foreach (var dependency in dependencies)
            {
                var satisfied = enabled.Any(other =>
                    other.Order < stage.Order &&
                    dependency.AnyOf.Contains(other.Type, StringComparer.OrdinalIgnoreCase));
                if (!satisfied)
                {
                    var display = displayNames.TryGetValue(stage.Type, out var d) ? d : stage.Type;
                    problems.Add($"'{display}' richiede almeno uno tra [{string.Join(", ", dependency.AnyOf)}] abilitato e ordinato prima.");
                }
            }
        }
        return problems;
    }
}
