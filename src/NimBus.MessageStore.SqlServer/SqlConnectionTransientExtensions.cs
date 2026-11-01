using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Routes every Dapper operation issued by the SQL store through the same
/// transient-exception translation policy. The <see cref="SqlConnection"/>
/// receiver is more specific than Dapper's <see cref="IDbConnection"/>
/// extensions, so store call sites use these overloads automatically.
/// </summary>
internal static class SqlConnectionTransientExtensions
{
    public static Task<int> ExecuteAsync(
        this SqlConnection connection,
        string sql,
        object? param = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null) =>
        SqlServerExceptionTranslation.TranslateAsync(() =>
            SqlMapper.ExecuteAsync(connection, sql, param, transaction, commandTimeout, commandType));

    public static Task<int> ExecuteAsync(
        this SqlConnection connection,
        CommandDefinition command) =>
        SqlServerExceptionTranslation.TranslateAsync(() =>
            SqlMapper.ExecuteAsync(connection, command));

    public static Task<IEnumerable<dynamic>> QueryAsync(
        this SqlConnection connection,
        string sql,
        object? param = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null) =>
        SqlServerExceptionTranslation.TranslateAsync(() =>
            SqlMapper.QueryAsync(connection, sql, param, transaction, commandTimeout, commandType));

    public static Task<IEnumerable<T>> QueryAsync<T>(
        this SqlConnection connection,
        string sql,
        object? param = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null) =>
        SqlServerExceptionTranslation.TranslateAsync(() =>
            SqlMapper.QueryAsync<T>(connection, sql, param, transaction, commandTimeout, commandType));

    public static Task<dynamic?> QueryFirstOrDefaultAsync(
        this SqlConnection connection,
        string sql,
        object? param = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null) =>
        SqlServerExceptionTranslation.TranslateAsync(() =>
            SqlMapper.QueryFirstOrDefaultAsync(connection, sql, param, transaction, commandTimeout, commandType));

    public static Task<T> QuerySingleAsync<T>(
        this SqlConnection connection,
        string sql,
        object? param = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null) =>
        SqlServerExceptionTranslation.TranslateAsync(() =>
            SqlMapper.QuerySingleAsync<T>(connection, sql, param, transaction, commandTimeout, commandType));

    public static Task<T?> QuerySingleOrDefaultAsync<T>(
        this SqlConnection connection,
        string sql,
        object? param = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null) =>
        SqlServerExceptionTranslation.TranslateAsync(() =>
            SqlMapper.QuerySingleOrDefaultAsync<T>(connection, sql, param, transaction, commandTimeout, commandType));

    public static Task<SqlMapper.GridReader> QueryMultipleAsync(
        this SqlConnection connection,
        string sql,
        object? param = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null) =>
        SqlServerExceptionTranslation.TranslateAsync(() =>
            SqlMapper.QueryMultipleAsync(connection, sql, param, transaction, commandTimeout, commandType));

    public static IAsyncEnumerable<dynamic> QueryUnbufferedAsync(
        this SqlConnection connection,
        string sql,
        object? param = null,
        DbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null) =>
        TranslateUnbuffered(SqlMapper.QueryUnbufferedAsync(
            connection, sql, param, transaction, commandTimeout, commandType));

    private static async IAsyncEnumerable<dynamic> TranslateUnbuffered(
        IAsyncEnumerable<dynamic> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var iterator = source.GetAsyncEnumerator(cancellationToken);
        while (await SqlServerExceptionTranslation.TranslateAsync(
            () => iterator.MoveNextAsync().AsTask()).ConfigureAwait(false))
        {
            yield return iterator.Current;
        }
    }
}
