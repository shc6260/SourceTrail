namespace Demo.Data;

// A deliberately inert test adapter: this fixture never connects to a database.
public static class Db
{
    public static void ExecuteProcedure(string procedure) { }
}
public sealed class Repository
{
    private const string SaveProcedure = "dbo.usp_TestSave";
    public void Save() => Db.ExecuteProcedure(SaveProcedure);
    public void Save(int count) => Save();
    public string CandidateOnly => "dbo.usp_NotCalled";
    // dbo.usp_CommentOnly must not become a procedure call.
    public void CycleA() => CycleB();
    public void CycleB() => CycleA();
    public void DelegateOnly() { Action callback = Save; }
}
