# episource.unblocker
A .net framework library to execute most<sup>*</sup> code - including blocking system calls - asynchronously with the ability to cancel the invocation at any time.

The invocation request is handled by an isolated child process. In case it has cancellation support built-in, it is used. If an attempt to cancel the an ongoing execution does not succeed within a given timeout (or cancellation is not supported at all), the execution is aborted in an crash-consistent manner:
 1. An attempt is made to unload the corresponding AppDomain
 2. If that fails, the chid process handling the invocation request is killed
 
Worker processes are created on demand and are reused for subsequent invocation requests. Processes are recycled if not used for a configurable amount of time.

For all this to work you must only stick to the following rules:
 - The invocation request must refer to serializable objects, only (invocation target, arguments, result)
 - Make sure you don't *unblock* any invocation that change global state in a way that is not safe when killed in the middle (database without transactions, files, ...)
 - Prefer unblocking static methods or using immutable invocation targets. The unblocker operates on an independent clone of the invocation target (via serialization). Any changes made by the task to the unblocker's instance are not reflected by the client's original instance. Consider using `UnblockerHost.InvoceMutableAsync` or `UnblockerHost.InvokeDetailedAsync` to retrieve the state of the unblocker's invocation target instance after the task was executed.  

# Quick start
All you need to do is create an instance of `UnblockerHost` and pass any *method call* lambda expression to its `InvokeAsync` method.

# Examples
## Managed sleep
Cancel managed code that blocks.
```cs
var unblocker = new UnblockerHost();
…
var cts = new CancellationTokenSource();
var sleepTask = unblocker.InvokeAsync(ct => Thread.Sleep(1000), cts.Token);

Thread.Sleep(100);
cts.Cancel();

try {
    await sleepTask;
} catch (TaskCanceledException) {
    Console.WriteLine("Sleep cancelled!");
}
```

## Native sleep
This works also when doing p/invoke!
```cs
public static class Native
{
    [DllImport("kernel32")]
    public static extern void Sleep(uint dwMilliseconds);
}
…   
var unblocker = new UnblockerHost();
…
var cts = new CancellationTokenSource();
var sleepTask = unblocker.InvokeAsync(ct => Native.Sleep(1000), cts.Token);

Thread.Sleep(100);
cts.Cancel();

try {
    await sleepTask;
} catch (TaskCanceledException) {
    Console.WriteLine("Sleep cancelled!");
}
```

## Return values
Return values also possible. Must be serializable, though.
```cs
[Serializable]
public class Job {
    private readonly int returnValue;
    
    public Job(int returnValue) {
        this.returnValue = returnValue;
    }

    public int GetValue() {
        return this.returnValue;
    } 
}
…
var unblocker = new UnblockerHost();
…
var j = new Job(10);
var val = await unblocker.InvokeAsync(ct => j.GetValue());
Console.WriteLine("Result: " + val);
```

## Mutable invocation target
Keep in mind, the unblocker worker operates on a clone of the invocation target. Therefore, changes to a mutable target won't be reflected by the client's instance. See below for an example, how to retrieve a worker's post invocation target instance.
```cs
[Serializable]
public class Job {
       
    public Job() {
        this.Counter = 0;
    }
    
    public int Counter { get; private set; }
    
    public int IncrementAndGet() {
        return ++this.Counter;
    }
}
…
var unblocker = new UnblockerHost();
…
var j = new Job();
Trace.Assert(j.Counter == 0);

var result = await unblocker.InvokeMutableAsync(j, (ct, _) => _.IncrementAndGet());

Trace.Assert(j.Counter == 0);
Trace.Assert(result.Result == 1);
Trace.Assert(result.PostInvocationTarget.Counter == 1);
```

## Debugging
There are two builtin options to debug tasks run by the unblocker:
 - Using **Console** output: To redirect console output, set `DebugMode.Console` — `new UnblockerHost(…, debug: DebugMode.Console)`.
 - Using **interactive debugger**: Set `DebugMode.Debugger` — `new UnblockerHost(…, debug: DebugMode.Debugger)`. The unblocker worker process than waits for an debugger to attach before running a task.