using System;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Ircuitry.Net;

/// <summary>
/// Runs raw SQL against a SQLite database file (the "advanced" half of the DB nodes).
/// SELECTs return formatted rows; other statements return the affected-row count.
/// </summary>
public static class Sql
{
    public static (string result, int rows, string? error) Run(string dbPath, string sql, int maxRows = 50)
    {
        if (string.IsNullOrWhiteSpace(dbPath)) return ("", 0, "no database path");
        if (string.IsNullOrWhiteSpace(sql)) return ("", 0, null);
        try
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;

            using var rd = cmd.ExecuteReader();
            if (rd.FieldCount == 0)            // non-query (INSERT/UPDATE/DDL)
                return ("", Math.Max(0, rd.RecordsAffected), null);

            var sb = new StringBuilder();
            int n = 0;
            while (rd.Read())
            {
                if (n < maxRows)
                {
                    var cells = new string[rd.FieldCount];
                    for (int i = 0; i < rd.FieldCount; i++) cells[i] = rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? "";
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(string.Join(" | ", cells));
                }
                n++;
            }
            return (sb.ToString(), n, null);
        }
        catch (Exception ex) { return ("", 0, ex.Message); }
    }
}
