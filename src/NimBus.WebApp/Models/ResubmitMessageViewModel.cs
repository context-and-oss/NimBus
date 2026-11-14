using System.Collections.Generic;
using NimBus.Core.Events;
using NimBus.WebApp.Constants;

namespace NimBus.WebApp.Models;

public class ResubmitMessageViewModel : ComposeNewMessageViewModel
{
    public string ErrorMessageId { get; set; } = null!;
}
