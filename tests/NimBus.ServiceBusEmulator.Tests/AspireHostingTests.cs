#pragma warning disable CA1707, CA2007

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class AspireHostingTests
{
    [TestMethod]
    public void Add_emulator_project_adds_the_project_by_path_and_its_connection_string()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var emulator = builder.AddNimBusServiceBusEmulatorProject("servicebus", FakeProject.FindProject());

        IResourceBuilder<DotnetProjectResource> project = emulator.Project;
        Assert.AreEqual("servicebus-emulator", project.Resource.Name);
        Assert.AreEqual("servicebus", emulator.ConnectionString.Resource.Name);
        StringAssert.Contains(
            emulator.ConnectionString.Resource.ConnectionStringExpression.ValueExpression,
            "UseDevelopmentEmulator=true",
            StringComparison.Ordinal);
        Assert.IsTrue(
            project.Resource.Annotations.OfType<EndpointAnnotation>().Any(endpoint => endpoint.Name == "tcp"),
            "The emulator's AMQP and admin endpoint.");
        Assert.IsTrue(
            project.Resource.Annotations.OfType<HealthCheckAnnotation>().Any(check => check.Key == "servicebus-emulator-readiness"),
            "Consumers wait on the connection string, which waits for the emulator to be ready.");
    }

    // The two overloads below are obsolete since the move to Aspire 17. They stay until the next
    // major, so these tests pin that they still work.
#pragma warning disable CS0618
    [TestMethod]
    public void Add_emulator_returns_the_compile_proven_two_resource_handle()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var emulator = builder.AddNimBusServiceBusEmulator<FakeProject>("servicebus");

        Assert.AreEqual("servicebus-emulator", emulator.Project.Resource.Name);
        Assert.AreEqual("servicebus", emulator.ConnectionString.Resource.Name);
        StringAssert.Contains(
            emulator.ConnectionString.Resource.ConnectionStringExpression.ValueExpression,
            "UseDevelopmentEmulator=true",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void Add_emulator_supports_an_explicit_project_path()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var emulator = builder.AddNimBusServiceBusEmulator("localbus", FakeProject.FindProject());

        Assert.AreEqual("localbus-emulator", emulator.Project.Resource.Name);
        Assert.AreEqual("localbus", emulator.ConnectionString.Resource.Name);
    }
#pragma warning restore CS0618

    private sealed class FakeProject : IProjectMetadata
    {
        public string ProjectPath => FindProject();

        internal static string FindProject()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "src",
                    "NimBus.ServiceBusEmulator",
                    "NimBus.ServiceBusEmulator.csproj");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the emulator project.");
        }
    }
}
