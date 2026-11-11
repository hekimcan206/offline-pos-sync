using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PosSync.Chaos;
using PosSync.Server.Persistence;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PosSync.IntegrationTests.Infrastructure.SyncEnvironment))]

namespace PosSync.IntegrationTests.Infrastructure;

/// <summary>
/// The whole server side, real: PostgreSQL 17 (initialised with the compose role script, migrated as
/// <c>sync_migrator</c>), the sync server on Kestrel connecting as <c>sync_app</c>, and a Toxiproxy container in front
/// of it. Terminals in the partition tests talk to the server only through Toxiproxy, so a disabled proxy or a toxic is
/// a real TCP-level fault (refused connections, cut responses, delayed bytes), not a mocked exception.
/// </summary>
/// <remarks>
/// Toxiproxy runs in Docker and the server runs on the host. Testcontainers' host-port exposure (an SSH reverse tunnel
/// to <c>host.testcontainers.internal</c>) connects them the same way on Docker Desktop and on Linux CI runners, without
/// opening an inbound firewall port. Each test uses its own store ids, so tests never need a database reset.
/// </remarks>
public sealed class SyncEnvironment : IAsyncLifetime
{
    public const string AdminKey = "admin-test-only";
    public const string Link = "terminal-link";
    public const string SlowLink = "slow-link";
    private const string MigratorPassword = "migrator-test";
    private const string AppPassword = "app-test";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("possync")
        .WithEnvironment("MIGRATOR_PASSWORD", MigratorPassword)
        .WithEnvironment("APP_PASSWORD", AppPassword)
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
            "/docker-entrypoint-initdb.d/01-roles.sh")
        .Build();

    private IContainer? _toxiproxy;
    private ServerFactory? _server;

    public int ServerPort { get; } = FreePort();

    /// <summary>Direct address of the server (no proxy).</summary>
    public Uri ServerUri => new($"http://127.0.0.1:{ServerPort}/");

    /// <summary>Address terminals use: through the Toxiproxy proxy.</summary>
    public Uri LinkUri { get; private set; } = null!;

    /// <summary>A second proxy to the same server, used to make two requests from one device arrive out of order.</summary>
    public Uri SlowLinkUri { get; private set; } = null!;

    public ToxiproxyClient Toxiproxy { get; private set; } = null!;

    public string SuperuserConnectionString => _postgres.GetConnectionString();

    public string MigratorConnectionString => WithCredentials("sync_migrator", MigratorPassword);

    public string AppConnectionString => WithCredentials("sync_app", AppPassword);

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await using (var dataSource = PersistenceExtensions.CreateDataSource(MigratorConnectionString))
        {
            await new MigrationRunner(dataSource, NullLogger<MigrationRunner>.Instance).MigrateAsync();
        }

        _server = new ServerFactory(AppConnectionString);
        _server.UseKestrel(ServerPort);
        _server.StartServer();

        await TestcontainersSettings.ExposeHostPortsAsync((ushort)ServerPort);
        _toxiproxy = new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
            .WithPortBinding(8474, true)
            .WithPortBinding(8666, true)
            .WithPortBinding(8667, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8474).ForPath("/version")))
            .Build();
        await _toxiproxy.StartAsync();

        Toxiproxy = new ToxiproxyClient(new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_toxiproxy.GetMappedPublicPort(8474)}/") });
        var upstream = $"host.testcontainers.internal:{ServerPort}";
        await Toxiproxy.CreateProxyAsync(Link, "0.0.0.0:8666", upstream);
        await Toxiproxy.CreateProxyAsync(SlowLink, "0.0.0.0:8667", upstream);
        LinkUri = new Uri($"http://127.0.0.1:{_toxiproxy.GetMappedPublicPort(8666)}/");
        SlowLinkUri = new Uri($"http://127.0.0.1:{_toxiproxy.GetMappedPublicPort(8667)}/");

        await WarmUpAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (_toxiproxy is not null)
        {
            await _toxiproxy.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public HttpClient Admin()
    {
        var client = new HttpClient { BaseAddress = ServerUri, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        return client;
    }

    public NpgsqlDataSource DataSource(string connectionString) => PersistenceExtensions.CreateDataSource(connectionString);

    private async Task WarmUpAsync()
    {
        // First requests through a fresh tunnel and a cold server (JIT, pool) are slow; pay that here, not in a test.
        using var direct = new HttpClient { BaseAddress = ServerUri, Timeout = TimeSpan.FromSeconds(60) };
        using var proxied = new HttpClient { BaseAddress = LinkUri, Timeout = TimeSpan.FromSeconds(60) };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                (await direct.GetAsync(new Uri("health/ready", UriKind.Relative))).EnsureSuccessStatusCode();
                (await proxied.GetAsync(new Uri("health/ready", UriKind.Relative))).EnsureSuccessStatusCode();
                using var admin = Admin();
                (await admin.PostAsJsonAsync("api/v1/admin/devices", new { nodeId = "warmup", storeId = "warmup" })).EnsureSuccessStatusCode();
                return;
            }
            catch (HttpRequestException) when (attempt < 20)
            {
                await Task.Delay(500);
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private string WithCredentials(string username, string password) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Username = username,
            Password = password,
            MaxPoolSize = 40,
            // Generous connect timeout: the host is shared with other projects' containers.
            Timeout = 60,
        }.ConnectionString;

    private sealed class ServerFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Sync:AdminKey", AdminKey);
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        }
    }
}
