using GhostClawUI.App.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostClawUI.App.Views;

internal sealed class QuickPromptWindow : Window
{
    private readonly Func<string, Task> _send;
    private readonly TextBox _prompt = UiKit.TextBox("Ask GhostClaw", "Quick prompt");

    public QuickPromptWindow(Func<string, Task> send)
    {
        _send = send;
        Title = "GhostClaw Quick Prompt";
        Content = Build();
    }

    private UIElement Build()
    {
        var panel = new StackPanel
        {
            Padding = new Thickness(18),
            Spacing = 12
        };
        panel.Children.Add(UiKit.Text("Quick prompt", 20, Microsoft.UI.Text.FontWeights.SemiBold));
        _prompt.AcceptsReturn = true;
        _prompt.MinHeight = 82;
        panel.Children.Add(_prompt);
        panel.Children.Add(UiKit.Button("Send", Symbol.Send, async (_, _) =>
        {
            var text = _prompt.Text.Trim();
            if (text.Length > 0)
            {
                await _send(text);
            }
            Close();
        }));
        return panel;
    }
}



