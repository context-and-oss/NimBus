#pragma warning disable CA1707, CA2007
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

[TestClass]
public sealed class SqlEntraAuthenticationProviderTests
{
    // SqlClient 7 ships the Entra ID providers in Microsoft.Data.SqlClient.Extensions.Azure.
    // Without it, an "Authentication=Active Directory ..." connection string fails at login.
    [TestMethod]
    [DataRow(SqlAuthenticationMethod.ActiveDirectoryDefault)]
    [DataRow(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    public void Entra_authentication_provider_is_registered(SqlAuthenticationMethod method)
    {
        Assert.IsNotNull(SqlAuthenticationProvider.GetProvider(method));
    }
}
