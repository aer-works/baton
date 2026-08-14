using Aer.Flow.Templates;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Aer.Ui.Tests;

/// <summary>
/// M16 Phase 1's walking skeleton (issue #150), driven through the real <see cref="MainWindow"/>:
/// create a new template or open an existing one, edit its metadata, save — and prove the saved
/// file round-trips through the engine's own <see cref="WorkflowDefinitionParser"/>/
/// <c>WorkflowDefinitionValidator</c>, with Flow spec §11.1's version-increment rule implemented
/// exactly (increment on a content-changing save; never on a no-op save; an explicit user-set
/// version respected; a first save of a new template unaffected).
/// </summary>
public class MainWindowTemplateEditorTests
{
    private static string FixturePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static string TempTemplatePath()
        => Path.Combine(Path.GetTempPath(), $"ui-template-editor-{Guid.NewGuid():N}.json");

    /// <summary>Copies a fixture to a temp path so a test can save over it without touching the shared fixture set.</summary>
    private static string CopyFixtureToTemp(string fileName)
    {
        var path = TempTemplatePath();
        File.Copy(FixturePath(fileName), path);
        return path;
    }

    [AvaloniaFact]
    public async Task A_new_template_is_created_metadata_edited_saved_and_engine_valid()
    {
        var path = TempTemplatePath();
        try
        {
            var window = new MainWindow();

            window.NewTemplate();
            Assert.True(window.ViewModel.TemplateEditor.IsOpen);
            // A never-yet-saved template is dirty by construction — nothing on disk matches it.
            Assert.True(window.ViewModel.TemplateEditor.IsDirty);

            window.ViewModel.TemplateEditor.TemplateId = "authored-in-ui";
            await window.SaveTemplateAsync(path, TestContext.Current.CancellationToken);

            var parsed = await WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("authored-in-ui", parsed.WorkflowTemplateId.Value);
            // A first save has no saved predecessor to distinguish from (§11.1) — no increment.
            Assert.Equal(1, parsed.WorkflowTemplateVersion);
            Assert.Empty(parsed.Steps);

            Assert.False(window.ViewModel.TemplateEditor.IsDirty);
            Assert.Contains("Saved", window.ViewModel.TemplateEditor.StatusText);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Editing_an_opened_templates_metadata_increments_the_version_and_preserves_its_steps()
    {
        var path = CopyFixtureToTemp("three-step-linear-workflow.json");
        try
        {
            var original = await WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);

            var window = new MainWindow();
            await window.OpenTemplateInEditorAsync(path, TestContext.Current.CancellationToken);

            Assert.True(window.ViewModel.TemplateEditor.IsOpen);
            Assert.False(window.ViewModel.TemplateEditor.IsDirty);
            Assert.Equal(original.WorkflowTemplateId.Value, window.ViewModel.TemplateEditor.TemplateId);
            Assert.Equal(original.WorkflowTemplateVersion.ToString(), window.ViewModel.TemplateEditor.TemplateVersionText);

            window.ViewModel.TemplateEditor.TemplateId = "renamed-template";
            Assert.True(window.ViewModel.TemplateEditor.IsDirty);

            await window.SaveTemplateAsync(path, TestContext.Current.CancellationToken);

            var saved = await WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("renamed-template", saved.WorkflowTemplateId.Value);
            // A content-changing save the user did not explicitly re-version increments (§11.1).
            Assert.Equal(original.WorkflowTemplateVersion + 1, saved.WorkflowTemplateVersion);
            // Steps are not editable until Phase 2 — they must ride through a metadata save untouched.
            Assert.Equal(original.Steps.Count, saved.Steps.Count);
            for (var i = 0; i < original.Steps.Count; i++)
            {
                Assert.Equal(original.Steps[i].StepId, saved.Steps[i].StepId);
                Assert.Equal(original.Steps[i].Worker, saved.Steps[i].Worker);
                Assert.Equal(original.Steps[i].DependsOn, saved.Steps[i].DependsOn);
            }

            // The incremented version reflects back into the editor rather than diverging from disk.
            Assert.Equal(saved.WorkflowTemplateVersion.ToString(), window.ViewModel.TemplateEditor.TemplateVersionText);
            Assert.False(window.ViewModel.TemplateEditor.IsDirty);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task A_no_op_save_writes_nothing_and_never_increments()
    {
        var path = CopyFixtureToTemp("three-step-linear-workflow.json");
        try
        {
            var contentBefore = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

            var window = new MainWindow();
            await window.OpenTemplateInEditorAsync(path, TestContext.Current.CancellationToken);
            await window.SaveTemplateAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal("No changes to save.", window.ViewModel.TemplateEditor.StatusText);
            Assert.Equal(contentBefore, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task An_explicitly_edited_version_is_respected_as_is()
    {
        var path = CopyFixtureToTemp("three-step-linear-workflow.json");
        try
        {
            var window = new MainWindow();
            await window.OpenTemplateInEditorAsync(path, TestContext.Current.CancellationToken);

            window.ViewModel.TemplateEditor.TemplateId = "renamed-template";
            window.ViewModel.TemplateEditor.TemplateVersionText = "7";
            await window.SaveTemplateAsync(path, TestContext.Current.CancellationToken);

            var saved = await WorkflowDefinitionParser.LoadFromFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(7, saved.WorkflowTemplateVersion);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task A_non_numeric_version_renders_in_window_and_writes_nothing()
    {
        var path = TempTemplatePath();
        try
        {
            var window = new MainWindow();
            window.NewTemplate();
            window.ViewModel.TemplateEditor.TemplateId = "bad-version";
            window.ViewModel.TemplateEditor.TemplateVersionText = "not-a-number";

            await window.SaveTemplateAsync(path, TestContext.Current.CancellationToken);

            Assert.Contains("not-a-number", window.ViewModel.TemplateEditor.StatusText);
            Assert.False(File.Exists(path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Opening_a_missing_file_in_the_editor_renders_in_window_and_leaves_no_stale_session()
    {
        var window = new MainWindow();
        window.NewTemplate();

        var missingPath = Path.Combine(Path.GetTempPath(), $"ui-template-editor-missing-{Guid.NewGuid():N}.json");
        await window.OpenTemplateInEditorAsync(missingPath, TestContext.Current.CancellationToken);

        Assert.False(window.ViewModel.TemplateEditor.IsOpen);
        Assert.Contains(missingPath, window.ViewModel.TemplateEditor.StatusText);

        // With no session, Save has nothing to act on — an in-window message, never a write.
        await window.SaveTemplateAsync(missingPath, TestContext.Current.CancellationToken);
        Assert.Contains("Nothing to save", window.ViewModel.TemplateEditor.StatusText);
        Assert.False(File.Exists(missingPath));
    }

    [AvaloniaFact]
    public async Task Saving_without_a_target_path_renders_in_window_and_writes_nothing()
    {
        var window = new MainWindow();
        window.NewTemplate();
        window.ViewModel.TemplateEditor.TemplateId = "no-path";

        await window.SaveTemplateAsync(string.Empty, TestContext.Current.CancellationToken);

        Assert.Contains("Enter a template file path", window.ViewModel.TemplateEditor.StatusText);
    }

    // #1222 retired The_read_only_template_view_is_untouched_by_the_editor_surface. It pinned M14
    // Phase 3's routing decision — "OpenAsync on a template file renders the read-only DAG view and
    // never starts an editing session" — and the first half of that is the thing #1222 deleted:
    // there is no read-only template view, because a workflow file is not a room and Author is the
    // one door to a shape — see 02-screens.md's #1222 amendment. The half that survives, that opening
    // a file by path never silently starts an editing session, moved into NavigationShellTests
    // rather than being dropped.
}
