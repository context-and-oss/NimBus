using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NimBus.MessageStore.States;

public class SearchResponse
{
    public IEnumerable<UnresolvedEvent> Events { get; set; } = null!;
    public string? ContinuationToken { get; set; }
}
