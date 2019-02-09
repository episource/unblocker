# episource.unblocker
A .net framework library to execute most<sup>*</sup> code - including blocking system calls - asynchronously with the ability to cancel the invocation at any time.

The invocation request is handled by an isolated child process. In case it has cancellation support built-in, it is used. If an attempt to cancel the an ongoing execution does not succeed within a given timeout (or cancellation is not supported at all), the execution is aborted in an crash-consistent manner:
 1. An attempt is made to unload the corresponding AppDomain
 2. If that fails, the chid process handling the invocation request is killed
 
Worker processes are created on demand and are reused for subsequent invocation requests. Processes are recycled if not used for a configurable amount of time.

For all this to work you must only stick to the following rules:
 - The invocation request must refer to serializable objects, only (invocation target, arguments, result)
 - Make sure you don't *unblock* any invocation that change global state in a way that is not safe when killed in the middle (database without transactions, files, ...)

# Quick start
All you need to do is create an instance of `Unblocker` and pass any *method call* lambda expression to its `InvokeAsync` method.

# Examples
## Managed sleep
Cancel managed code that blocks.
```cs
var unblocker = new Unblocker();
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
var unblocker = new Unblocker();
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
var unblocker = new Unblocker();
…
var j = new Job(10);
var val = await unblocker.InvokeAsync(ct => j.GetValue());
Console.WriteLine("Result: " + val);
```

