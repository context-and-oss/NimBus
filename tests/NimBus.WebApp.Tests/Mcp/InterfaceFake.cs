#pragma warning disable CA1707, CA2007
using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// A fake for a large interface (the NSwag-generated controller contracts): only the methods
/// a test configures respond; any other call throws. Records every call's arguments.
/// </summary>
public class InterfaceFake<T> : DispatchProxy where T : class
{
    private readonly Dictionary<string, Func<object?[], object?>> _handlers = new(StringComparer.Ordinal);

    /// <summary>Calls received, in order.</summary>
    public ConcurrentQueue<(string Method, object?[] Args)> Calls { get; } = new();

    /// <summary>Creates the proxy and returns its configuration handle.</summary>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Mirrors DispatchProxy.Create<T, TProxy>(): the factory needs T.")]
    public static InterfaceFake<T> Create()
    {
        var proxy = Create<T, InterfaceFake<T>>();
        var fake = (InterfaceFake<T>)(object)proxy;
        fake.Instance = proxy;
        return fake;
    }

    /// <summary>The proxy to register as <typeparamref name="T"/>.</summary>
    public T Instance { get; private set; } = null!;

    /// <summary>Configures the response for <paramref name="method"/>.</summary>
    public InterfaceFake<T> On(string method, Func<object?[], object?> handler)
    {
        _handlers[method] = handler;
        return this;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var arguments = args ?? [];
        Calls.Enqueue((targetMethod.Name, arguments));
        return _handlers.TryGetValue(targetMethod.Name, out var handler)
            ? handler(arguments)
            : throw new NotImplementedException($"{typeof(T).Name}.{targetMethod.Name} is not configured on this fake.");
    }
}
