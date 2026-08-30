using Microsoft.Data.SqlClient;

namespace Negosio.IntegrationTests.Platform;

/// <summary>Tiny raw-SQL helpers for asserting straight against a tenant's physical database.</summary>
internal static class PlatformSql
{
    public static async Task<int> CountAsync(SqlConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [{table}]";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public static async Task<List<string>> StringsAsync(SqlConnection connection, string sql)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public static async Task<bool> DatabaseExistsAsync(SqlConnection master, string databaseName)
    {
        await using var command = master.CreateCommand();
        command.CommandText = "SELECT DB_ID(@name)";
        command.Parameters.AddWithValue("@name", databaseName);
        return await command.ExecuteScalarAsync() is not (null or DBNull);
    }
}
