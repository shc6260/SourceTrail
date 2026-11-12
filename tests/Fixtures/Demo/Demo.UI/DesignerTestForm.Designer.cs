namespace Demo.UI;

public sealed partial class DesignerTestForm
{
    private void InitializeComponent()
    {
        this._button.Click += new System.EventHandler(this.SaveClicked);
    }
}
