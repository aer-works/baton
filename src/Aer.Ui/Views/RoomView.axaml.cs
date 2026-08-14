using Avalonia.Controls;

namespace Aer.Ui.Views;

/// <summary>
/// The room's shape (M19 Phase 3, #188): the DAG as the primary surface with per-step drill-in,
/// plain-language primary text, and the full precise record in the Details disclosure. Rendering is
/// driven by the shell (<c>MainWindow</c>), which owns the session.
/// <para>
/// Since #1196 slice 3 it no longer carries the room's decisions — those moved, template and
/// commands unchanged, into the transcript in <c>ChatView</c>, where a decision is answered where it
/// was raised. This view is what remains: what the room IS, rather than what it is asking.
/// </para>
/// </summary>
public partial class RoomView : UserControl
{
    public RoomView() => InitializeComponent();
}
