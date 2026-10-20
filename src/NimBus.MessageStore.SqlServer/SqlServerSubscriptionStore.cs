using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

internal sealed class SqlServerSubscriptionStore : ISubscriptionStore
{
    private readonly SqlServerStoreContext _context;

    public SqlServerSubscriptionStore(SqlServerStoreContext context) => _context = context;

    private Task<SqlConnection> OpenAsync() => _context.Open();

    private string T(string table) => _context.Table(table);
    // ───────── Subscription store ─────────

    public async Task<EndpointSubscription?> SubscribeToEndpointNotification(string endpointId, string mail, string type, string author, string url, List<string> eventTypes, string payload, int frequency)
    {
        var sub = new EndpointSubscription
        {
            Id = Guid.NewGuid().ToString(),
            EndpointId = endpointId,
            Mail = mail,
            Type = type,
            AuthorId = author,
            Url = url,
            EventTypes = eventTypes,
            Payload = payload,
            Frequency = frequency,
        };
        var sql = $@"
INSERT INTO {T("EndpointSubscriptions")} (Id, EndpointId, Type, Mail, AuthorId, Url, EventTypesJson, Payload, Frequency)
VALUES (@Id, @EndpointId, @Type, @Mail, @AuthorId, @Url, @EventTypesJson, @Payload, @Frequency)";
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, new
        {
            sub.Id, sub.EndpointId, sub.Type, sub.Mail, sub.AuthorId, sub.Url,
            EventTypesJson = JsonConvert.SerializeObject(eventTypes),
            sub.Payload, sub.Frequency
        }, commandTimeout: _context.CommandTimeout);
        return sub;
    }

    public async Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("EndpointSubscriptions")} WHERE EndpointId = @E",
            new { E = endpointId }, commandTimeout: _context.CommandTimeout);
        return rows.Select(MapSubscriptionRow).ToList();
    }

    public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpoint, string eventtypes, string payload, string errorText)
        => GetSubscriptionsOnEndpoint(endpoint);

    public async Task<bool> UpdateSubscription(EndpointSubscription subscription)
    {
        var sql = $@"
UPDATE {T("EndpointSubscriptions")}
SET Mail = @Mail, Type = @Type, Url = @Url, EventTypesJson = @EventTypesJson, Payload = @Payload, Frequency = @Frequency
WHERE Id = @Id";
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, new
        {
            subscription.Id, subscription.Mail, subscription.Type, subscription.Url,
            EventTypesJson = JsonConvert.SerializeObject(subscription.EventTypes),
            subscription.Payload, subscription.Frequency
        }, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    public async Task<bool> UnsubscribeById(string endpointId, string id)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"DELETE FROM {T("EndpointSubscriptions")} WHERE Id = @Id AND EndpointId = @E",
            new { Id = id, E = endpointId }, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    public async Task<bool> UnsubscribeByMail(string endpointId, string mail)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"DELETE FROM {T("EndpointSubscriptions")} WHERE Mail = @Mail AND EndpointId = @E",
            new { Mail = mail, E = endpointId }, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    public async Task<bool> DeleteSubscription(string subscriptionId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"DELETE FROM {T("EndpointSubscriptions")} WHERE Id = @Id",
            new { Id = subscriptionId }, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    private static EndpointSubscription MapSubscriptionRow(dynamic row) => new()
    {
        Id = row.Id,
        EndpointId = row.EndpointId,
        Type = row.Type,
        NotificationSeverity = row.NotificationSeverity,
        Mail = row.Mail,
        AuthorId = row.AuthorId,
        NotifiedAt = row.NotifiedAt,
        ErrorList = row.ErrorList,
        Url = row.Url,
        EventTypes = string.IsNullOrEmpty((string?)row.EventTypesJson)
            ? new List<string>()
            : JsonConvert.DeserializeObject<List<string>>((string)row.EventTypesJson) ?? new List<string>(),
        Payload = row.Payload,
        Frequency = row.Frequency,
    };


}
