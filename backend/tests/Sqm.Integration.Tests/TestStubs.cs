using System.Reflection;

namespace Sqm.Integration.Tests;

/// <summary>Interface stubs for host-level tests that need no database.</summary>
/// <remarks>
/// Built on DispatchProxy, so a stub answers only the members a test names and throws on any
/// other - an unexpected call is a failure, not a silent default.
/// </remarks>
public static class TestStubs
{
    public static T Create<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler;
        return proxy;
    }

    /// <summary>Forwards every interface call to a handler.</summary>
    public class Proxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args ?? []);
    }
}
