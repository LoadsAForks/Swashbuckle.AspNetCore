﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;

namespace Microsoft.Extensions.Hosting;

internal sealed class HostFactoryResolver
{
    private const BindingFlags DeclaredOnlyLookup = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public const string BuildWebHost = nameof(BuildWebHost);
    public const string CreateWebHostBuilder = nameof(CreateWebHostBuilder);
    public const string CreateHostBuilder = nameof(CreateHostBuilder);

    // The amount of time we wait for the diagnostic source events to fire
    private static readonly TimeSpan s_defaultWaitTimeout = Debugger.IsAttached ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(30);

    public static Func<string[], TWebHost> ResolveWebHostFactory<TWebHost>(Assembly assembly)
    {
        return ResolveFactory<TWebHost>(assembly, BuildWebHost);
    }

    public static Func<string[], TWebHostBuilder> ResolveWebHostBuilderFactory<TWebHostBuilder>(Assembly assembly)
    {
        return ResolveFactory<TWebHostBuilder>(assembly, CreateWebHostBuilder);
    }

    public static Func<string[], THostBuilder> ResolveHostBuilderFactory<THostBuilder>(Assembly assembly)
    {
        return ResolveFactory<THostBuilder>(assembly, CreateHostBuilder);
    }

    // This helpers encapsulates all of the complex logic required to:
    // 1. Execute the entry point of the specified assembly in a different thread.
    // 2. Wait for the diagnostic source events to fire
    // 3. Give the caller a chance to execute logic to mutate the IHostBuilder
    // 4. Resolve the instance of the applications's IHost
    // 5. Allow the caller to determine if the entry point has completed
    public static Func<string[], object> ResolveHostFactory(
        Assembly assembly,
        TimeSpan? waitTimeout = null,
        bool stopApplication = true,
        Action<object> configureHostBuilder = null,
        Action<Exception> entrypointCompleted = null)
    {
        if (assembly.EntryPoint is null)
        {
            return null;
        }

        try
        {
            // Attempt to load hosting and check the version to make sure the events
            // even have a chance of firing (they were added in .NET >= 6)
            var hostingAssembly = Assembly.Load("Microsoft.Extensions.Hosting");
            if (hostingAssembly.GetName().Version is Version version && version.Major < 6)
            {
                return null;
            }

            // We're using a version >= 6 so the events can fire. If they don't fire
            // then it's because the application isn't using the hosting APIs
        }
        catch
        {
            // There was an error loading the extensions assembly, return null.
            return null;
        }

        return args => new HostingListener(args, assembly.EntryPoint, waitTimeout ?? s_defaultWaitTimeout, stopApplication, configureHostBuilder, entrypointCompleted).CreateHost();
    }

    private static Func<string[], T> ResolveFactory<T>(Assembly assembly, string name)
    {
        var programType = assembly?.EntryPoint?.DeclaringType;
        if (programType == null)
        {
            return null;
        }

        var factory = programType.GetMethod(name, DeclaredOnlyLookup);
        if (!IsFactory<T>(factory))
        {
            return null;
        }

        return args => (T)factory.Invoke(null, [args]);
    }

    // TReturn Factory(string[] args);
    private static bool IsFactory<TReturn>(MethodInfo factory)
    {
        return factory != null
            && typeof(TReturn).IsAssignableFrom(factory.ReturnType)
            && factory.GetParameters().Length == 1
            && typeof(string[]).Equals(factory.GetParameters()[0].ParameterType);
    }

    // Used by EF tooling without any Hosting references. Looses some return type safety checks.
    public static Func<string[], IServiceProvider> ResolveServiceProviderFactory(Assembly assembly, TimeSpan? waitTimeout = null)
    {
        // Prefer the older patterns by default for back compat.
        var webHostFactory = ResolveWebHostFactory<object>(assembly);
        if (webHostFactory != null)
        {
            return args =>
            {
                var webHost = webHostFactory(args);
                return GetServiceProvider(webHost);
            };
        }

        var webHostBuilderFactory = ResolveWebHostBuilderFactory<object>(assembly);
        if (webHostBuilderFactory != null)
        {
            return args =>
            {
                var webHostBuilder = webHostBuilderFactory(args);
                var webHost = Build(webHostBuilder);
                return GetServiceProvider(webHost);
            };
        }

        var hostBuilderFactory = ResolveHostBuilderFactory<object>(assembly);
        if (hostBuilderFactory != null)
        {
            return args =>
            {
                var hostBuilder = hostBuilderFactory(args);
                var host = Build(hostBuilder);
                return GetServiceProvider(host);
            };
        }

        var hostFactory = ResolveHostFactory(assembly, waitTimeout: waitTimeout);
        if (hostFactory != null)
        {
            return args =>
            {
                var host = hostFactory(args);
                return GetServiceProvider(host);
            };
        }

        return null;
    }

    private static object Build(object builder)
    {
        var buildMethod = builder.GetType().GetMethod("Build");
        return buildMethod?.Invoke(builder, []);
    }

    private static IServiceProvider GetServiceProvider(object host)
    {
        if (host == null)
        {
            return null;
        }
        var hostType = host.GetType();
        var servicesProperty = hostType.GetProperty("Services", DeclaredOnlyLookup);
        return (IServiceProvider)servicesProperty?.GetValue(host);
    }

    private sealed class HostingListener(
        string[] args,
        MethodInfo entryPoint,
        TimeSpan waitTimeout,
        bool stopApplication,
        Action<object> configure,
        Action<Exception> entrypointCompleted) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object>>
    {
        private readonly string[] _args = args;
        private readonly MethodInfo _entryPoint = entryPoint;
        private readonly TimeSpan _waitTimeout = waitTimeout;
        private readonly bool _stopApplication = stopApplication;

        private readonly TaskCompletionSource<object> _hostTcs = new();
        private IDisposable _disposable;
        private readonly Action<object> _configure = configure;
        private readonly Action<Exception> _entrypointCompleted = entrypointCompleted;
        private static readonly AsyncLocal<HostingListener> _currentListener = new();

        public object CreateHost()
        {
            using var subscription = DiagnosticListener.AllListeners.Subscribe(this);

            // Kick off the entry point on a new thread so we don't block the current one
            // in case we need to timeout the execution
            var thread = new Thread(() =>
            {
                Exception exception = null;

                try
                {
                    // Set the async local to the instance of the HostingListener so we can filter events that
                    // aren't scoped to this execution of the entry point.
                    _currentListener.Value = this;

                    var parameters = _entryPoint.GetParameters();
                    if (parameters.Length == 0)
                    {
                        _entryPoint.Invoke(null, []);
                    }
                    else
                    {
                        _entryPoint.Invoke(null, [_args]);
                    }

                    // Try to set an exception if the entry point returns gracefully, this will force
                    // build to throw
                    _hostTcs.TrySetException(new InvalidOperationException("Unable to build IHost"));
                }
                catch (TargetInvocationException tie) when (tie.InnerException is StopTheHostException)
                {
                    // The host was stopped by our own logic
                }
                catch (TargetInvocationException tie)
                {
                    exception = tie.InnerException ?? tie;

                    // Another exception happened, propagate that to the caller
                    _hostTcs.TrySetException(exception);
                }
                catch (Exception ex)
                {
                    exception = ex;

                    // Another exception happened, propagate that to the caller
                    _hostTcs.TrySetException(ex);
                }
                finally
                {
                    // Signal that the entry point is completed
                    _entrypointCompleted?.Invoke(exception);
                }
            })
            {
                // Make sure this doesn't hang the process
                IsBackground = true
            };

            // Start the thread
            thread.Start();

            try
            {
                // Wait before throwing an exception
                if (!_hostTcs.Task.Wait(_waitTimeout))
                {
                    throw new InvalidOperationException("Unable to build IHost");
                }
            }
            catch (AggregateException) when (_hostTcs.Task.IsCompleted)
            {
                // Lets this propagate out of the call to GetAwaiter().GetResult()
            }

            Debug.Assert(_hostTcs.Task.IsCompleted);

            return _hostTcs.Task.GetAwaiter().GetResult();
        }

        public void OnCompleted()
        {
            _disposable?.Dispose();
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(DiagnosticListener value)
        {
            if (_currentListener.Value != this)
            {
                // Ignore events that aren't for this listener
                return;
            }

            if (value.Name == "Microsoft.Extensions.Hosting")
            {
                _disposable = value.Subscribe(this);
            }
        }

        public void OnNext(KeyValuePair<string, object> value)
        {
            if (_currentListener.Value != this)
            {
                // Ignore events that aren't for this listener
                return;
            }

            if (value.Key == "HostBuilding")
            {
                _configure?.Invoke(value.Value);
            }

            if (value.Key == "HostBuilt")
            {
                _hostTcs.TrySetResult(value.Value);

                if (_stopApplication)
                {
                    // Stop the host from running further
                    throw new StopTheHostException();
                }
            }
        }

        private sealed class StopTheHostException : Exception;
    }
}
