using System.Reflection;

namespace Sqm.Ingestion.Tests;

/// <summary>Interface stubs built on DispatchProxy, for driving processors without a database.</summary>
public static class Stub
{
    public static T Create<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler;
        return proxy;
    }

    /// <summary>A completed task carrying the return type's default, for calls a test ignores.</summary>
    public static object? Default(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);

        var type = method.ReturnType;
        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var result = type.GenericTypeArguments[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(result)
                .Invoke(null, [result.IsValueType ? Activator.CreateInstance(result) : null]);
        }

        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }

    /// <summary>Forwards every interface call to a handler.</summary>
    public class Proxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Handler { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args ?? []);
    }
}
