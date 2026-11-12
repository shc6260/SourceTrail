using System.Windows.Forms;

namespace Demo.UI;

public sealed partial class DesignerTestForm : Form
{
    private readonly Button _button = new();
    public DesignerTestForm() { InitializeComponent(); }
    private void SaveClicked(object? sender, EventArgs e) { }
}
