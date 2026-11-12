using System.Data;
using Microsoft.Data.SqlClient;

namespace Demo.Data;

internal static class DatabaseCalls
{
    public static void Save(DataTable rows, SqlConnection connection, string dynamicName)
    {
        ReadPatient(connection);
        new SaveAdapter().Update(rows);

        using var command = new SqlCommand("dbo.usp_ClearFixture", connection);
        command.CommandType = CommandType.StoredProcedure;
        command.ExecuteNonQuery();

        using var unknown = new SqlCommand(dynamicName, connection);
        unknown.CommandType = CommandType.StoredProcedure;
        unknown.ExecuteNonQuery();

        using var unused = new SqlCommand("dbo.usp_UnusedFixture", connection);
        unused.CommandType = CommandType.StoredProcedure;
    }

    private static void ReadPatient(SqlConnection connection)
    {
        using var reader = new SqlCommand("SELECT Name FROM dbo.InfoPatient", connection);
        reader.ExecuteReader();
    }
}
