using System;
using System.Collections;
using System.Globalization;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Runtime.Remoting.Lifetime;
using System.Security;

namespace episource.unblocker.hosting {
    public sealed class WorkerServerClientSideProxy : MarshalByRefObject, IWorkerServer {
        private static readonly object proxyDomainLock = new object(); 
        private static AppDomain proxyDomain;
        private static int proxyDomainRefCount;
        
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

                    try {
                        proxyDomain.DoCallBack(SetupRemoting);
                    } catch (Exception) {
                        DecrementProxyRef(true);
                        throw;
                    }
                }

                try {
                    var proxy = (WorkerServerClientSideProxy) proxyDomain.CreateInstanceAndUnwrap(
                        t.Assembly.FullName, t.FullName);
                    proxy.Connect(ipcguid);
                    return proxy;
                } catch (Exception) {
                    DecrementProxyRef(true);
                    throw;
                }
            }
            
        }

        private readonly ClientSponsor remoteProxySponsor = new ClientSponsor();
        private IWorkerServer remoteProxy;
        
        public event EventHandler<TaskCanceledEventArgs> TaskCanceledEvent;
        public event EventHandler<TaskSucceededEventArgs> TaskSucceededEvent;
        public event EventHandler<TaskFailedEventArgs> TaskFailedEvent;
        public event EventHandler ServerDyingEvent;
        public event EventHandler ServerReadyEvent;
        
        public void Cancel(TimeSpan cancelTimeout) {
            this.remoteProxy.Cancel(cancelTimeout);
        }

        public void InvokeAsync(InvocationRequest.PortableInvocationRequest invocationRequest, SecurityZone securityZone) {
            this.remoteProxy.InvokeAsync(invocationRequest, securityZone);
        }

        // must be public to be bindable to remote events
        public void OnTaskCanceled(object sender, TaskCanceledEventArgs args) {
            this.TaskCanceledEvent.InvokeEvent(e => e( sender, args));
        }
        
        // must be public to be bindable to remote events
        public void OnTaskSucceeded(object sender, TaskSucceededEventArgs args) {
            this.TaskSucceededEvent.InvokeEvent(e => e( sender, args));
        }
        
        // must be public to be bindable to remote events
        public void OnTaskFailed(object sender, TaskFailedEventArgs args) {
            this.TaskFailedEvent.InvokeEvent(e => e( sender, args));
        }
        
        // must be public to be bindable to remote events
        public void OnServerDying(object sender, EventArgs args) {
            this.ServerDyingEvent.InvokeEvent(e => e( sender, args));
        }
        
        // must be public to be bindable to remote events
        public void OnServerReady(object sender, EventArgs args) {
            this.ServerReadyEvent.InvokeEvent(e => e( sender, args));
        }
        
        private void Connect(Guid ipcguid) {
            if (this.remoteProxy != null) {
                throw new InvalidOperationException("Already connected.");
            }
            
            var server = (WorkerServer)RemotingServices.Connect(typeof(WorkerServer),
                string.Format(CultureInfo.InvariantCulture,
                    @"ipc://{0}/{1}", ipcguid, typeof(WorkerServer).FullName)
            );
            this.remoteProxySponsor.Register(server);
            this.remoteProxy = server;
            
            this.remoteProxy.ServerDyingEvent += this.OnServerDying;
            this.remoteProxy.ServerReadyEvent += this.OnServerReady;
            this.remoteProxy.TaskFailedEvent += this.OnTaskFailed;
            this.remoteProxy.TaskCanceledEvent += this.OnTaskCanceled;
            this.remoteProxy.TaskSucceededEvent += this.OnTaskSucceeded;
        }

        // Limit remoting to specific appdomain: Channels cannot be unregistered and ensures that other services
        // within the application using this library are not exposed by the worker channel.
        private static void SetupRemoting() {
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
                    
                    this.remoteProxySponsor.Close();
                }
            }
            
            DecrementProxyRef(disposing);
        }

        private static void DecrementProxyRef(bool mayThrow) {
            lock (proxyDomainLock) {
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