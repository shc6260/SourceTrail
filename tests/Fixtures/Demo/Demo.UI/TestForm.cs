using Demo.Data;
using System.Windows.Forms;

namespace Demo.UI;

public interface ISaveService { void Save(); }
public sealed class Service : ISaveService
{
    private readonly Repository _repository = new();
    public void Save() => _repository.Save();
}
public sealed class TestForm : Form
{
    private readonly Button _button = new();
    private readonly ISaveService _service = new Service();
    public string Caption { get; set; } = "Demo";
    public TestForm() { _button.Click += SaveClicked; }
    private void SaveClicked(object? sender, EventArgs e) => _service.Save();
}
