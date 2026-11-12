using System.Windows.Forms;

namespace Demo.UI;

public sealed partial class DesignerTestForm : Form
{
    private readonly Button _button = new();
    private readonly Button _noteButton = new();
    public DesignerTestForm() { InitializeComponent(); }
    private void SaveClicked(object? sender, EventArgs e) => SaveData();
    private void SaveData() { }
    private void EditNoteClicked(object? sender, EventArgs e) { }
}

internal sealed class ActionHost
{
    private sealed record ActionItem(Action Open)
    {
        public string Caption { get; init; } = "";
    }

    private ActionItem BuildMenu() => new ActionItem(OpenStock) { Caption = "재고" };
    private void OpenStock() => new DesignerTestForm().Show();
}
