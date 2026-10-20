using NimBus.Core.Messages;
using System;

namespace NimBus.Testing;

public sealed record InMemoryDeliveryResult(
    IMessage OriginalMessage,
    InMemoryMessageContext Context,
    Exception? Exception);
