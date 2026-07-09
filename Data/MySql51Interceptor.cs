using System.Collections;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DOSApi.Data;

/// <summary>
/// MySQL 5.1 compatibility interceptor.
///
/// Pomelo 8.x batches modification commands into a single multi-statement SQL string
/// separated by semicolons, e.g.:
///
///   UPDATE `cart` SET ... WHERE `id` = ?;
///   SELECT ROW_COUNT();
///
///   INSERT INTO `cart_items` (...) VALUES (?);
///   SELECT `id` WHERE ROW_COUNT() = 1 AND `id` = LAST_INSERT_ID()
///
/// MySQL 5.1 rejects these without CLIENT_MULTI_STATEMENTS. This interceptor:
///   1. Detects any multi-statement command (contains ';' outside string literals).
///   2. Splits it into individual (DML, SELECT) pairs.
///   3. Executes each DML statement via ExecuteNonQueryAsync.
///   4. Reads the appropriate return value (ROW_COUNT or LAST_INSERT_ID) separately.
///   5. Returns a composite reader whose result sets satisfy Pomelo's
///      ConsumeResultSetAsync expectations.
///
/// Pomelo reads each command's result set by calling ReadAsync() directly (no
/// preceding NextResultAsync). After reading the row, it calls NextResultAsync()
/// to advance to the next command's result set.
/// </summary>
public sealed class MySql51Interceptor : DbCommandInterceptor
{
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        var sql = command.CommandText;
        if (!ContainsSemicolon(sql))
            return result;

        var stmts = SplitStatements(sql);

        // Group into (dml, checkSelect) pairs.  The DML is the substantive
        // operation; the immediately-following SELECT reads back either
        // ROW_COUNT() or LAST_INSERT_ID() to let Pomelo verify success and
        // propagate generated keys.
        var pairs = BuildPairs(stmts);
        if (pairs.Count == 0)
            return result;

        var conn = command.Connection!;
        var resultValues = new List<long>(pairs.Count);

        foreach (var (dmlSql, selectSql) in pairs)
        {
            using var dmlCmd = conn.CreateCommand();
            dmlCmd.CommandText = dmlSql;
            dmlCmd.Transaction = command.Transaction;
            CopyParameters(command.Parameters, dmlCmd);
            await dmlCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            long val;
            if (selectSql != null && selectSql.Contains("LAST_INSERT_ID", StringComparison.OrdinalIgnoreCase))
            {
                using var idCmd = conn.CreateCommand();
                idCmd.CommandText = "SELECT LAST_INSERT_ID()";
                idCmd.Transaction = command.Transaction;
                val = Convert.ToInt64(await idCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
            }
            else
            {
                // SELECT ROW_COUNT() — or no SELECT at all; return 1 to signal success
                using var rcCmd = conn.CreateCommand();
                rcCmd.CommandText = "SELECT ROW_COUNT()";
                rcCmd.Transaction = command.Transaction;
                val = Convert.ToInt64(await rcCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
            }

            resultValues.Add(val);
        }

        return InterceptionResult<DbDataReader>.SuppressWithResult(
            new CompositeResultReader(resultValues));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static bool ContainsSemicolon(string sql)
    {
        bool inSingleQuote = false, inDoubleQuote = false;
        foreach (char c in sql)
        {
            if (c == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
            else if (c == ';' && !inSingleQuote && !inDoubleQuote) return true;
        }
        return false;
    }

    private static List<string> SplitStatements(string sql)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inSingleQuote = false, inDoubleQuote = false;

        foreach (char c in sql)
        {
            if (c == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; sb.Append(c); }
            else if (c == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; sb.Append(c); }
            else if (c == ';' && !inSingleQuote && !inDoubleQuote)
            {
                var s = sb.ToString().Trim();
                if (s.Length > 0) result.Add(s);
                sb.Clear();
            }
            else sb.Append(c);
        }
        var last = sb.ToString().Trim();
        if (last.Length > 0) result.Add(last);
        return result;
    }

    /// <summary>Builds (dml, selectOrNull) pairs from the statement list.
    /// A DML statement (INSERT/UPDATE/DELETE) is paired with the SELECT that
    /// immediately follows it. Back-to-back DML statements are each paired with
    /// their own SELECT. Pure SELECT statements with no preceding DML are
    /// skipped (they don't correspond to a modification command batch slot).</summary>
    private static List<(string dml, string? select)> BuildPairs(List<string> stmts)
    {
        var pairs = new List<(string, string?)>();
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            if (IsDml(s))
            {
                string? sel = null;
                if (i + 1 < stmts.Count && IsSelect(stmts[i + 1]))
                {
                    sel = stmts[i + 1];
                    i++; // consume the SELECT too
                }
                pairs.Add((s, sel));
            }
            // pure SELECT lines not preceded by a DML are ignored
        }
        return pairs;
    }

    private static bool IsDml(string s) =>
        s.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);

    private static bool IsSelect(string s) =>
        s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);

    private static void CopyParameters(DbParameterCollection source, DbCommand target)
    {
        foreach (DbParameter p in source)
        {
            var np = target.CreateParameter();
            np.ParameterName = p.ParameterName;
            np.Value = p.Value;
            np.DbType = p.DbType;
            np.Direction = p.Direction;
            if (p is MySqlConnector.MySqlParameter mp && np is MySqlConnector.MySqlParameter mnp)
                mnp.MySqlDbType = mp.MySqlDbType;
            target.Parameters.Add(np);
        }
    }
}

/// <summary>
/// Composite DbDataReader with N result sets, one per DML command in the batch.
///
/// Each result set has exactly one row and one column:
///   • For INSERT commands: column 0 = LAST_INSERT_ID() value.
///   • For UPDATE/DELETE commands: column 0 = ROW_COUNT() value.
///
/// Pomelo's ConsumeResultSetAsync protocol (per batch slot):
///   1. Read() / ReadAsync()   → true once (the one row)
///   2. GetValue(0)             → the value above
///   3. Read() / ReadAsync()   → false (end of this result set)
///   4. NextResult()           → true if more slots remain, false when done
/// </summary>
internal sealed class CompositeResultReader : DbDataReader
{
    private readonly IReadOnlyList<long> _values; // one value per result set
    private int _currentSet = 0;
    private bool _rowRead = false;      // whether Read() has been called for the current set
    private bool _rowConsumed = false;  // whether Read() already returned true once

    internal CompositeResultReader(List<long> values) => _values = values;

    // ── navigation ────────────────────────────────────────────────────────

    public override bool Read()
    {
        if (_currentSet >= _values.Count) return false;
        if (_rowConsumed) return false;  // only one row per result set
        _rowRead = true;
        _rowConsumed = true;
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        => Task.FromResult(Read());

    public override bool NextResult()
    {
        if (_currentSet + 1 >= _values.Count) return false;
        _currentSet++;
        _rowRead = false;
        _rowConsumed = false;
        return true;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        => Task.FromResult(NextResult());

    // ── schema ────────────────────────────────────────────────────────────

    public override bool HasRows => _currentSet < _values.Count && !_rowConsumed;
    public override int FieldCount => _currentSet < _values.Count ? 1 : 0;
    public override bool IsClosed => false;
    public override int Depth => 0;
    public override int RecordsAffected => -1;

    public override string GetName(int ordinal) { ValidateOrdinal(ordinal); return "value"; }
    public override int GetOrdinal(string name) => 0;
    public override string GetDataTypeName(int ordinal) { ValidateOrdinal(ordinal); return "bigint"; }
    public override Type GetFieldType(int ordinal) { ValidateOrdinal(ordinal); return typeof(long); }

    // ── data access ───────────────────────────────────────────────────────

    public override object GetValue(int ordinal) { ValidateRowAccess(ordinal); return _values[_currentSet]; }
    public override int GetValues(object[] values) { if (values.Length > 0) values[0] = GetValue(0); return Math.Min(1, values.Length); }
    public override bool IsDBNull(int ordinal) { ValidateOrdinal(ordinal); return false; }

    public override long GetInt64(int ordinal) { ValidateRowAccess(ordinal); return _values[_currentSet]; }
    public override int GetInt32(int ordinal) { ValidateRowAccess(ordinal); return (int)_values[_currentSet]; }
    public override short GetInt16(int ordinal) { ValidateRowAccess(ordinal); return (short)_values[_currentSet]; }
    public override byte GetByte(int ordinal) { ValidateRowAccess(ordinal); return (byte)_values[_currentSet]; }
    public override decimal GetDecimal(int ordinal) { ValidateRowAccess(ordinal); return _values[_currentSet]; }
    public override double GetDouble(int ordinal) { ValidateRowAccess(ordinal); return _values[_currentSet]; }
    public override float GetFloat(int ordinal) { ValidateRowAccess(ordinal); return _values[_currentSet]; }
    public override string GetString(int ordinal) { ValidateRowAccess(ordinal); return _values[_currentSet].ToString(); }
    public override bool GetBoolean(int ordinal) { ValidateRowAccess(ordinal); return _values[_currentSet] != 0; }
    public override Guid GetGuid(int ordinal) => throw new InvalidCastException();
    public override DateTime GetDateTime(int ordinal) => throw new InvalidCastException();
    public override char GetChar(int ordinal) { ValidateRowAccess(ordinal); return (char)_values[_currentSet]; }
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(0);

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override DataTable? GetSchemaTable() => null;

    private void ValidateOrdinal(int ordinal)
    { if (ordinal != 0) throw new IndexOutOfRangeException($"ordinal {ordinal}"); }

    private void ValidateRowAccess(int ordinal)
    {
        if (!_rowRead) throw new InvalidOperationException("Call Read() before accessing row data.");
        ValidateOrdinal(ordinal);
    }
}
