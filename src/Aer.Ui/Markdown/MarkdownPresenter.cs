using Avalonia;
using Avalonia.Controls;

namespace Aer.Ui.Markdown;

public class MarkdownPresenter : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownPresenter, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MarkdownProperty)
        {
            Content = MarkdownRenderer.Render(change.GetNewValue<string?>());
        }
    }
}
