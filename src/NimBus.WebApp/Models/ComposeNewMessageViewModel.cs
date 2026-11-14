using System.Collections.Generic;
using NimBus.Core.Events;

namespace NimBus.WebApp.Models;

public class ComposeNewMessageViewModel : EditMessageViewModel
{
    public string EndpointId { get; set; } = null!;
    public IEnumerable<string> EventTypes { get; set; } = null!;
    public string SelectedEventType { get; set; } = null!;
    public Dictionary<string, IEvent> EventTemplates { get; set; } = null!;


    public const string DropDownElementId = "SelectedEventType";
    public const string MessageElementId = "EventMessageJsonTextArea";
    public const string TemplateDumpElementId = "EventTemplateDump";
}
