namespace Demo.UI;

public sealed partial class DesignerTestForm
{
    private void InitializeComponent()
    {
        this.Text = "자재 관리";
        this._button.Text = "수정(F12)";
        this._button.Click += new System.EventHandler(this.SaveClicked);
        this._noteButton.Text = "수정";
        this._noteButton.Click += new System.EventHandler(this.EditNoteClicked);
    }
}
