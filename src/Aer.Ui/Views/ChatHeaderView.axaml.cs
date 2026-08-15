using Avalonia.Controls;

namespace Aer.Ui.Views;

/// <summary>
/// The room header (#1224), a view of its own only because it must live one level up from
/// <see cref="ChatView"/> — above the transcript/shape split rather than inside the transcript
/// column. Its markup is <c>ChatHeaderView.axaml</c>'s and moved there unchanged apart from the
/// arrangement that fixes the clipping; the shell (<c>MainWindow</c>) still owns the wiring of every
/// button on it, exactly as it did when these controls sat in ChatView.
/// </summary>
public partial class ChatHeaderView : UserControl
{
    public ChatHeaderView()
    {
        InitializeComponent();
    }
}
