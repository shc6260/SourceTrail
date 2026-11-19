using System.Data;
using Microsoft.Data.SqlClient;

namespace Demo.Data;

internal static class CollectionCalls
{
    public static void Save(SqlConnection connection, bool changed)
    {
        var commands = new List<SqlCommand>();
        if (changed)
        {
            var insert = new SqlCommand("dbo.usp_CollectionInsert");
            insert.CommandType = CommandType.StoredProcedure;
            commands.Add(insert);
        }
        var update = new SqlCommand("dbo.usp_CollectionUpdate") { CommandType = CommandType.StoredProcedure };
        commands.Add(update);
        var unused = new SqlCommand("dbo.usp_UnusedCollection") { CommandType = CommandType.StoredProcedure };
        ExecuteBatch(connection, commands.ToArray());
    }

    public static void Array(SqlConnection connection)
    {
        var command = new SqlCommand("dbo.usp_ArrayFixture") { CommandType = CommandType.StoredProcedure };
        var commands = new[] { command };
        ExecuteBatch(connection, commands);
    }

    public static void Dynamic(SqlConnection connection, string name)
    {
        var commands = new List<SqlCommand>();
        var command = new SqlCommand(name) { CommandType = CommandType.StoredProcedure };
        commands.Add(command);
        ExecuteBatch(connection, commands.ToArray());
    }

    public static void Escaped(SqlConnection connection)
    {
        var commands = new List<SqlCommand>();
        var command = new SqlCommand("dbo.usp_Escaped") { CommandType = CommandType.StoredProcedure };
        var alias = command;
        alias.CommandText = "dbo.usp_Changed";
        commands.Add(command);
        ExecuteBatch(connection, commands.ToArray());
    }

    public static void MutatedCollection(SqlConnection connection)
    {
        var commands = new List<SqlCommand>();
        var command = new SqlCommand("dbo.usp_Removed") { CommandType = CommandType.StoredProcedure };
        commands.Add(command);
        commands.Clear();
        ExecuteBatch(connection, commands.ToArray());
    }

    public static void MutatedWrapper(SqlConnection connection)
    {
        var commands = new List<SqlCommand>();
        var command = new SqlCommand("dbo.usp_Original") { CommandType = CommandType.StoredProcedure };
        commands.Add(command);
        ExecuteChanged(connection, commands.ToArray());
    }

    public static void ConditionalMutation(SqlConnection connection, bool changed)
    {
        var commands = new List<SqlCommand>();
        var command = new SqlCommand("dbo.usp_Original") { CommandType = CommandType.StoredProcedure };
        if (changed) command.CommandText = "dbo.usp_Changed";
        commands.Add(command);
        ExecuteBatch(connection, commands.ToArray());
    }

    private static void ExecuteBatch(SqlConnection connection, params SqlCommand[] commands)
    {
        foreach (var command in commands)
        {
            command.Connection = connection;
            command.CommandTimeout = 600;
            command.ExecuteNonQuery();
            command.Parameters.Clear();
        }
    }

    private static void ExecuteChanged(SqlConnection connection, params SqlCommand[] commands)
    {
        foreach (var command in commands)
        {
            command.CommandText = "dbo.usp_Changed";
            command.Connection = connection;
            command.ExecuteNonQuery();
        }
    }
}
