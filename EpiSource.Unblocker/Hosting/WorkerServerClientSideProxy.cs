using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Runtime.Remoting.Lifetime;
using System.Security;

namespace EpiSource.Unblocker.Hosting {
    /// Limit remoting to specific appdomain: Channels cannot be unregistered and ensures that other services
    /// within the application using this library are not exposed by the worker channel.
    public sealed class WorkerServerClientSideProxy : MarshalByRefObject, IWorkerServer {
        private sealed class ProxyDomainSetup : MarshalByRefObject {

            public void Setup() {
                AppDomain.CurrentDomain.AssemblyResolve += ResolveCurrentAssemblyAcrossLoadContext;
                
                // register explicit channel to be compatible with the server side
                // needed when using callbacks
                IDictionary ipcProperties = new Hashtable();
                ipcProperties["name"] = "UnblockerClientChannel";
                ipcProperties["portName"] = Guid.NewGuid().ToString();
                ipcProperties["typeFilterLevel"] = "Full";
                var ipcChannel = new IpcChannel(ipcProperties,
                    new BinaryClientFormatterSinkProvider(ipcProperties, null),
                    new BinaryServerFormatterSinkProvider(ipcProperties, null));
                ChannelServices.RegisterChannel(ipcChannel, false);
            }
        }
        
        private static readonly object proxyDomainLock = new object(); 
        private static AppDomain proxyDomain;
        private static int proxyDomainRefCount;
        
        // Limit remoting to specific appdomain: Channels cannot be unregistered and ensures that other services
        // within the application using this library are not exposed by the worker channel.
        public static WorkerServerClientSideProxy ConnectToWorkerServer(Guid ipcguid) {
            var t = typeof(WorkerServerClientSideProxy);
            var remotingDomainDame = string.Format(CultureInfo.InvariantCulture, "{0}_{1}",
                typeof(WorkerServerClientSideProxy).Name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            
            lock (proxyDomainLock) {
                if (proxyDomainRefCount == 0) {
                    var remotingDomain = AppDomain.CreateDomain(remotingDomainDame, AppDomain.CurrentDomain.Evidence,
                        new AppDomainSetup {
                            ApplicationBase = AppDomain.CurrentDomain.BaseDirectory,
                            LoaderOptimization = LoaderOptimization.MultiDomainHost
                        },
                        AppDomain.CurrentDomain.PermissionSet, t.GetStrongNameOfAssemblyAsArray());
                    
                    proxyDomain = remotingDomain;
                    proxyDomainRefCount++;

                    AppDomain.CurrentDomain.AssemblyResolve += ResolveCurrentAssemblyAcrossLoadContext;
                    
                    // Important: Load current assembly based on path to support corner cases with the unblocker
                    // assembly not being available from the assembly search path!
                    
                    try {
                        var setupType = typeof(ProxyDomainSetup);
                        var setup = (ProxyDomainSetup) proxyDomain.CreateInstanceFromAndUnwrap(
                            setupType.Assembly.Location, setupType.FullName);
                        setup.Setup();
                    } catch (Exception) {
                        DecrementProxyRef(true);
                        throw;
                    }
                }

                try {
                    var proxy = (WorkerServerClientSideProxy) proxyDomain.CreateInstanceFromAndUnwrap(
                        t.Assembly.Location, t.FullName);
                    proxy.Connect(ipcguid);
                    return proxy;
                } catch (Exception) {
                    DecrementProxyRef(true);
                    throw;
                }
            }
            
        }

        private readonly ClientSponsor proxySponsor = new ClientSponsor();
        private IWorkerServer remoteProxy;

        private event EventHandler<PortableEventArgs<TaskCanceledEventArgs>> taskCanceledEvent;
        public event EventHandler<PortableEventArgs<TaskCanceledEventArgs>> TaskCanceledEvent {
            add { this.taskCanceledEvent += this.registerRemoteHandler(value); }
            remove { this.taskCanceledEvent -= this.unregisterRemoteHandler(value); }
        }

        private event EventHandler<PortableEventArgs<TaskSucceededEventArgs>> taskSucceededEvent;
        public event EventHandler<PortableEventArgs<TaskSucceededEventArgs>> TaskSucceededEvent {
            add { this.taskSucceededEvent += this.registerRemoteHandler(value); }
            remove { this.taskSucceededEvent -= this.unregisterRemoteHandler(value); }
        }

        private event EventHandler<PortableEventArgs<TaskFailedEventArgs>> taskFailedEvent;
        public event EventHandler<PortableEventArgs<TaskFailedEventArgs>> TaskFailedEvent {
            add { this.taskFailedEvent += this.registerRemoteHandler(value); }
            remove { this.taskFailedEvent -= this.unregisterRemoteHandler(value); }
        }
        
        private event EventHandler serverDyingEvent;
        public event EventHandler ServerDyingEvent {
            add { this.serverDyingEvent += this.registerRemoteHandler(value); }
            remove { this.serverDyingEvent -= this.unregisterRemoteHandler(value); }
        }
        
        private event EventHandler serverReadyEvent;
        public event EventHandler ServerReadyEvent {
            add { this.serverReadyEvent += this.registerRemoteHandler(value); }
            remove { this.serverReadyEvent -= this.unregisterRemoteHandler(value); }
        }
        
        public void Cancel(TimeSpan cancelTimeout, ForcedCancellationMode forcedCancellationMode) {
            this.remoteProxy.Cancel(cancelTimeout, forcedCancellationMode);
        }

        public void InvokeAsync(InvocationRequest.PortableInvocationRequest invocationRequest, SecurityZone securityZone) {
            this.remoteProxy.InvokeAsync(invocationRequest, securityZone);
        }

        // must be public to be bindable to remote events
        public void OnTaskCanceled(object sender, PortableEventArgs<TaskCanceledEventArgs> args) {
            this.taskCanceledEvent.InvokeEvent(e => e( sender, args));
        }
        
        // must be public to be bindable to remote events
        public void OnTaskSucceeded(object sender, PortableEventArgs<TaskSucceededEventArgs> args) {
            this.taskSucceededEvent.InvokeEvent(e => e( sender, args));
        }
        
        // must be public to be bindable to remote events
        public void OnTaskFailed(object sender, PortableEventArgs<TaskFailedEventArgs> args) {
            this.taskFailedEvent.InvokeEvent(e => e( sender, args));
        }
        
        // must be public to be bindable to remote events
        public void OnServerDying(object sender, EventArgs args) {
            this.serverDyingEvent.InvokeEvent(e => e( sender, args));
        }
        
        // must be public to be bindable to remote events
        public void OnServerReady(object sender, EventArgs args) {
            this.serverReadyEvent.InvokeEvent(e => e( sender, args));
        }
        
        private void Connect(Guid ipcguid) {
            if (this.remoteProxy != null) {
                throw new InvalidOperationException("Already connected.");
            }
            
            var server = (WorkerServer)RemotingServices.Connect(typeof(WorkerServer),
                string.Format(CultureInfo.InvariantCulture,
                    @"ipc://{0}/{1}", ipcguid, typeof(WorkerServer).FullName)
            );
            this.proxySponsor.Register(server);
            this.remoteProxy = server;
            
            this.remoteProxy.ServerDyingEvent += this.OnServerDying;
            this.remoteProxy.ServerReadyEvent += this.OnServerReady;
            this.remoteProxy.TaskFailedEvent += this.OnTaskFailed;
            this.remoteProxy.TaskCanceledEvent += this.OnTaskCanceled;
            this.remoteProxy.TaskSucceededEvent += this.OnTaskSucceeded;
        }

        private T registerRemoteHandler<T>(T handler) {
            var handlerDelegate = handler as Delegate;
            if (handlerDelegate == null) {
                return handler;
            }
            
            var targetRefObject = handlerDelegate.Target as MarshalByRefObject;
            if (targetRefObject != null) {
                this.proxySponsor.Register(targetRefObject);
            }

            return handler;
        }

        private T unregisterRemoteHandler<T>(T handler) {
            var handlerDelegate = handler as Delegate;
            if (handlerDelegate == null) {
                return handler;
            }
            
            var targetRefObject = handlerDelegate.Target as MarshalByRefObject;
            if (targetRefObject != null) {
                this.proxySponsor.Unregister(targetRefObject);
            }

            return handler;
        }
        
        // Proxy Domain loads current assembly in load from context.
        // This handler resolves it using the current context.
        private static Assembly ResolveCurrentAssemblyAcrossLoadContext(object sender, ResolveEventArgs e) {
            if (e.Name == typeof(WorkerServerClientSideProxy).Assembly.FullName) {
                return typeof(WorkerServerClientSideProxy).Assembly;
            }

            return null;
        }
        
        #region Dispose & Cleanup

        public void Dispose() {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        
        private /*protected virtual*/ void Dispose(bool disposing) {
            if (disposing) {
                if (this.remoteProxy != null) {
                    this.remoteProxy.ServerDyingEvent -= this.OnServerDying;
                    this.remoteProxy.ServerReadyEvent -= this.OnServerReady;
                    this.remoteProxy.TaskFailedEvent -= this.OnTaskFailed;
                    this.remoteProxy.TaskCanceledEvent -= this.OnTaskCanceled;
                    this.remoteProxy.TaskSucceededEvent -= this.OnTaskSucceeded;
                    
                    this.remoteProxy.Dispose();
                    this.remoteProxy = null;
                    
                    this.proxySponsor.Close();
                }
            }
            
            DecrementProxyRef(disposing);
        }

        private static void DecrementProxyRef(bool mayThrow) {
            lock (proxyDomainLock) {
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveCurrentAssemblyAcrossLoadContext;
                
                proxyDomainRefCount--;
                if (proxyDomainRefCount == 0 && proxyDomain != null) {
                    try {
                        AppDomain.Unload(proxyDomain);
                    } catch (CannotUnloadAppDomainException) {
                        if (mayThrow) {
                            throw;
                        }
                    }

                    proxyDomain = null;
                }
            }
        }

        ~WorkerServerClientSideProxy() {
            this.Dispose(false);   
        }

        #endregion
        
    }
}