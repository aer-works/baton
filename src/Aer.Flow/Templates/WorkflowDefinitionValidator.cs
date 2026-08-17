using Aer.Flow.Domain;

namespace Aer.Flow.Templates;

/// <summary>
/// Structural validation for a <see cref="WorkflowDefinition"/> template (spec §11.1), independent
/// of how the definition was produced (parsed from a file, constructed in-memory, etc).
/// </summary>
public static class WorkflowDefinitionValidator
{
    /// <summary>
    /// Validates <paramref name="definition"/>, throwing <see cref="WorkflowDefinitionValidationException"/>
    /// with every violation found if it is not well-formed. A well-formed definition has:
    /// unique <see cref="StepId"/>s; every <c>DependsOn</c> entry resolving to a declared
    /// <see cref="StepId"/>; an acyclic <c>DependsOn</c> graph; a <c>RetryPolicy</c> with
    /// <c>MaxAttempts &gt;= 1</c> on every step (§10); and, for every declared <c>PausePoint</c>,
    /// <c>SupersedeTargets</c> entries that are each either a transitive ancestor of the declaring
    /// step or the declaring step itself (§17.1) — the latter is the repeated-supersede-self chain
    /// M24's chat primitive depends on; <see cref="Mutation.ExternalDecisionValidator"/> already
    /// admits it at the decision level, this was the one gate that had not been updated to match.
    /// </summary>
    public static void Validate(WorkflowDefinition definition)
    {
        // System.Text.Json does not enforce non-nullable reference-typed record parameters by
        // default: a template JSON that simply omits "steps", "dependsOn", or "supersedeTargets"
        // deserializes those properties as null despite their declared (non-nullable) type. Every
        // list is re-checked for null below so a hand-edited or truncated template file fails with
        // a WorkflowDefinitionValidationException, not an unhandled NullReferenceException.
        var errors = new List<string>();

        if (definition.Steps is null)
        {
            throw new WorkflowDefinitionValidationException(["WorkflowDefinition.Steps is missing."]);
        }

        for (var i = 0; i < definition.Steps.Count; i++)
        {
            if (definition.Steps[i] is null)
            {
                errors.Add($"WorkflowDefinition.Steps[{i}] is missing.");
            }
        }

        if (errors.Count > 0)
        {
            throw new WorkflowDefinitionValidationException(errors);
        }

        var declared = new HashSet<StepId>();
        foreach (var step in definition.Steps)
        {
            if (!declared.Add(step.StepId))
            {
                errors.Add($"Duplicate StepId '{step.StepId}'.");
            }

            if (step.Outputs is not null)
            {
                foreach (var output in step.Outputs)
                {
                    if (ReservedOutputNames.IsReserved(output))
                    {
                        errors.Add($"Step '{step.StepId}' declares output '{output}', which is rejected: {ReservedOutputNames.RejectionClause}.");
                    }
                }
            }
        }

        foreach (var step in definition.Steps)
        {
            if (step.DependsOn is null)
            {
                errors.Add($"Step '{step.StepId}' has a missing DependsOn list.");
                continue;
            }

            foreach (var dependency in step.DependsOn)
            {
                if (!declared.Contains(dependency))
                {
                    errors.Add($"Step '{step.StepId}' declares DependsOn '{dependency}', which is not a declared StepId.");
                }
            }
        }

        foreach (var step in definition.Steps)
        {
            if (step.RetryPolicy is null)
            {
                errors.Add($"Step '{step.StepId}' has a missing RetryPolicy.");
            }
            else if (step.RetryPolicy.MaxAttempts < 1)
            {
                errors.Add(
                    $"Step '{step.StepId}' declares RetryPolicy.MaxAttempts '{step.RetryPolicy.MaxAttempts}', which must be at least 1.");
            }
        }

        // The ancestor walk below assumes every StepId reference resolves, appears once, and has
        // a non-null DependsOn list; surface the errors above first rather than let the walk fail
        // on malformed input.
        if (errors.Count > 0)
        {
            throw new WorkflowDefinitionValidationException(errors);
        }

        var ancestorsByStep = ComputeTransitiveAncestorsCore(definition, errors);

        foreach (var step in definition.Steps)
        {
            if (step.PausePoint is null)
            {
                continue;
            }

            if (step.PausePoint.SupersedeTargets is null)
            {
                errors.Add($"Step '{step.StepId}' declares a PausePoint with a missing SupersedeTargets list.");
                continue;
            }

            var ancestors = ancestorsByStep[step.StepId];
            foreach (var target in step.PausePoint.SupersedeTargets)
            {
                if (target != step.StepId && !ancestors.Contains(target))
                {
                    errors.Add(
                        $"Step '{step.StepId}' declares SupersedeTarget '{target}', which is not a transitive ancestor of '{step.StepId}' and is not the step itself.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new WorkflowDefinitionValidationException(errors);
        }
    }

    /// <summary>
    /// Computes every step's full set of transitive <c>DependsOn</c> ancestors — the same walk
    /// <see cref="Validate"/> performs internally for <c>SupersedeTargets</c> ancestry, exposed here
    /// for an editor's <c>SupersedeTargets</c> candidate list (M16 Phase 3, issue #152): "reflect,
    /// don't invent" needs the live in-edit graph's actual ancestor set, not a caller-side
    /// re-implementation of this rule (the authoring counterpart of "Flow carries discipline").
    /// Assumes <paramref name="definition"/> is already structurally valid — unique <c>StepId</c>s,
    /// every <c>DependsOn</c> reference resolvable, acyclic — the same precondition a live DAG-layout
    /// preview carries; call only after <see cref="Validate"/> has succeeded, or this throws or
    /// produces meaningless results on malformed input, exactly like an unguarded graph walk would.
    /// </summary>
    public static IReadOnlyDictionary<StepId, IReadOnlySet<StepId>> ComputeTransitiveAncestors(WorkflowDefinition definition)
    {
        var ancestorsByStep = ComputeTransitiveAncestorsCore(definition, errors: []);
        return ancestorsByStep.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<StepId>)kv.Value);
    }

    /// <summary>
    /// Computes, for every step, the full set of StepIds reachable by following <c>DependsOn</c>
    /// edges backward. Detects cycles along the way — a cyclic <c>DependsOn</c> graph contradicts
    /// §11.1's "no loops" static-DAG requirement, and would otherwise make "transitive ancestor"
    /// an ill-defined question for <c>SupersedeTargets</c> validation.
    /// </summary>
    private static Dictionary<StepId, HashSet<StepId>> ComputeTransitiveAncestorsCore(
        WorkflowDefinition definition,
        List<string> errors)
    {
        var byId = definition.Steps.ToDictionary(step => step.StepId);
        var ancestorsByStep = new Dictionary<StepId, HashSet<StepId>>();
        var inProgress = new HashSet<StepId>();

        HashSet<StepId> Visit(StepId stepId)
        {
            if (ancestorsByStep.TryGetValue(stepId, out var cached))
            {
                return cached;
            }

            if (!inProgress.Add(stepId))
            {
                errors.Add($"Cyclic DependsOn graph detected at StepId '{stepId}'.");
                return [];
            }

            var ancestors = new HashSet<StepId>();
            foreach (var dependency in byId[stepId].DependsOn)
            {
                ancestors.Add(dependency);
                ancestors.UnionWith(Visit(dependency));
            }

            inProgress.Remove(stepId);
            ancestorsByStep[stepId] = ancestors;
            return ancestors;
        }

        foreach (var step in definition.Steps)
        {
            Visit(step.StepId);
        }

        return ancestorsByStep;
    }
}
