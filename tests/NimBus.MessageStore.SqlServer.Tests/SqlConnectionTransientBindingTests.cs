#pragma warning disable CA1707, CA2007
using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.MessageStore.SqlServer.Tests;

/// <summary>
/// <see cref="SqlConnectionTransientExtensions"/> works by being <em>more specific</em> than
/// Dapper's own <see cref="System.Data.IDbConnection"/> extensions, so a store call site picks it
/// up silently. The failure mode is equally silent: call a Dapper method the shim class does not
/// overload and the call binds straight to <see cref="SqlMapper"/>, losing transient translation,
/// so a deadlock or command timeout reaches the Resolver as a raw <see cref="SqlException"/> and
/// dead-letters the message instead of being rescheduled.
///
/// <para>These tests read back the method C# actually bound for a <see cref="SqlConnection"/>
/// receiver. They fail at the moment a store starts using an unshielded Dapper method — which is
/// what happened when the Spec 030 MERGE moved from <c>ExecuteAsync</c> to
/// <c>QuerySingleAsync</c>.</para>
/// </summary>
[TestClass]
public sealed class SqlConnectionTransientBindingTests
{
    [TestMethod]
    public void QuerySingleAsync_binds_to_the_translating_overload()
    {
        // The Spec 030 stale-write guard reads its applied/refused answer from SELECT @@ROWCOUNT,
        // and the same statement now takes a HOLDLOCK range lock, which makes deadlocks likelier.
        AssertTranslated<int>(connection => connection.QuerySingleAsync<int>("SELECT 1"));
    }

    [TestMethod]
    public void ExecuteAsync_binds_to_the_translating_overload()
    {
        AssertTranslated<int>(connection => connection.ExecuteAsync("SELECT 1"));
    }

    [TestMethod]
    public void ExecuteAsync_with_CommandDefinition_binds_to_the_translating_overload()
    {
        // The retention job passes a CommandDefinition so shutdown can cancel a running DELETE.
        AssertTranslated<int>(connection => connection.ExecuteAsync(new CommandDefinition("SELECT 1")));
    }

    [TestMethod]
    public void QuerySingleOrDefaultAsync_binds_to_the_translating_overload()
    {
        AssertTranslated<int>(connection => connection.QuerySingleOrDefaultAsync<int>("SELECT 1"));
    }

    private static void AssertTranslated<T>(Expression<Func<SqlConnection, Task<T>>> call)
    {
        var bound = ((MethodCallExpression)call.Body).Method;

        Assert.AreEqual(
            typeof(SqlConnectionTransientExtensions),
            bound.DeclaringType,
            $"'{bound.Name}' bound to {bound.DeclaringType?.Name} instead of the transient-translating " +
            $"shim. Add a {nameof(SqlConnection)} overload to {nameof(SqlConnectionTransientExtensions)}, " +
            "or the SQL store will dead-letter messages on retryable errors.");
    }
}
