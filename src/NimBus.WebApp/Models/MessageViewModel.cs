using NimBus.MessageStore;
using NimBus.WebApp.Services.ApplicationInsights;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Models;

public class MessageViewModel
{
    public MessageEntity FailedMessage { get; set; } = null!;
    public MessageEntity OriginatingMessage { get; set; } = null!;
    public IEnumerable<LogEntry> LogEntries { get; set; } = null!;
    public IEnumerable<MessageEntity> Actions { get; set; } = null!;
}
