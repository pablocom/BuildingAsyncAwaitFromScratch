using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

await PrintAsync();

static async Task PrintAsync()
{
    for (int i = 0; ; i++)
    {
        await MyTask.Delay(1000);

        if (i == 5)
            throw new Exception("Boom!");

        Console.WriteLine(i);
    }
}

return;

Console.Write("Hello, ");
MyTask.Delay(4000).ContinueWith(delegate
{
    Console.Write("World! ");
    return MyTask.Delay(2000).ContinueWith(delegate
    {
        Console.Write("And Paula! ");
        return MyTask.Delay(1000).ContinueWith(delegate
        {
            Console.Write("How you doing?");
        });
    });
}).Wait();

Console.ReadLine();

var myAsyncLocalValue = new AsyncLocal<int>();
var tasks = new List<MyTask>();
for (int i = 0; i < 100; i++)
{
    myAsyncLocalValue.Value = i;

    tasks.Add(MyTask.Run(delegate
    {
        Console.WriteLine(myAsyncLocalValue.Value);
        Thread.Sleep(1000);
    }));
}

MyTask.WhenAll(tasks).Wait();

class MyTask
{
    private bool _completed;
    private Exception? _exception;
    private Action? _continuation;
    private ExecutionContext? _context;

    private object _lock = new();

    public struct MyTaskAwaiter(MyTask t) : INotifyCompletion
    {
        public MyTaskAwaiter GetAwaiter() => this;
        public bool IsCompleted => t.IsCompleted;
        public void OnCompleted(Action continuation) => t.ContinueWith(continuation);
        public void GetResult() => t.Wait();
    }

    public MyTaskAwaiter GetAwaiter() => new(this);

    public bool IsCompleted
    {
        get
        {
            lock (_lock)
            {
                return _completed;
            }
        }
    }

    public void SetResult() => Complete(null);
    public void SetException(Exception exception) => Complete(exception);

    private void Complete(Exception? exception)
    {
        lock (_lock)
        {
            if (_completed)
                throw new InvalidOperationException("It's already completed!!");

            _completed = true;
            _exception = exception;

            if (_continuation is not null)
            {
                MyThreadPool.QueueUserWorkItem(delegate
                {
                    if (_context is null)
                        _continuation();
                    else
                        ExecutionContext.Run(_context, static (object? state) => ((Action)state!).Invoke(), _continuation);
                });
            }
        }
    }

    public void Wait() 
    {
        ManualResetEventSlim? mres = null;

        lock (_lock)
        {
            if (!_completed)
            {
                mres = new ManualResetEventSlim();
                ContinueWith(mres.Set);
            }
        }

        mres?.Wait();

        if (_exception is not null)
        {
            ExceptionDispatchInfo.Throw(_exception);
        }
    }

    public MyTask ContinueWith(Action action) 
    {
        MyTask t = new();

        Action callback = () =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                t.SetException(ex);
                return;
            }

            t.SetResult();
        };

        lock (_lock)
        {
            if (_completed)
            {
                MyThreadPool.QueueUserWorkItem(callback);
            }
            else
            {
                _continuation = callback;
                _context = ExecutionContext.Capture();
            }
        }

        return t;
    }

    public MyTask ContinueWith(Func<MyTask> action)
    {
        MyTask t = new();

        Action callback = () =>
        {
            try
            {
                MyTask next = action();
                next.ContinueWith(delegate
                {
                    if (next._exception is not null)
                    {
                        t.SetException(next._exception);
                    }
                    else
                    {
                        t.SetResult();
                    }
                });

            }
            catch (Exception e)
            {
                t.SetException(e);
                return;
            }
        };

        lock (_lock)
        {
            if (_completed)
            {
                MyThreadPool.QueueUserWorkItem(callback);
            }
            else
            {
                _continuation = callback;
                _context = ExecutionContext.Capture();
            }
        }

        return t;
    }

    public static MyTask Run(Action action)
    {
        var t = new MyTask();

        MyThreadPool.QueueUserWorkItem(() =>
        {
            try 
            { 
                action(); 
            } 
            catch (Exception ex) 
            { 
                t.SetException(ex);
                return;
            }

            t.SetResult();
        });

        return t;
    }

    public static MyTask WhenAll(List<MyTask> tasks)
    {
        MyTask t = new MyTask();

        if (tasks.Count is 0)
        {
            t.SetResult();
        }
        else
        {
            int remaining = tasks.Count;
            Action continuation = () =>
            {
                if (Interlocked.Decrement(ref remaining) == 0)
                {
                    t.SetResult();
                }
            };

            foreach (var task in tasks)
            {
                task.ContinueWith(continuation);
            }
        }

        return t;
    }
    
    public static MyTask Delay(int timeout)
    {
        MyTask t = new();

        new Timer(_ => t.SetResult()).Change(timeout, -1);

        return t;
    }

    public static MyTask Iterate(IEnumerable<MyTask> tasks)
    {
        MyTask t = new MyTask();

        IEnumerator<MyTask> enumerator = tasks.GetEnumerator();
        void MoveNext()
        {
            try
            {
                if (enumerator.MoveNext())
                {
                    MyTask next = enumerator.Current;
                    next.ContinueWith(MoveNext);
                    return;
                }
            }
            catch (Exception ex)
            {
                t.SetException(ex);
                return;
            }
            

            t.SetResult();
        }

        MoveNext();

        return t;
    }
}

static class MyThreadPool
{
    private static readonly BlockingCollection<(Action, ExecutionContext?)> s_workItems = new();
    public static void QueueUserWorkItem(Action action) => s_workItems.Add((action, ExecutionContext.Capture()));

     static MyThreadPool()
    {
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            new Thread(() => 
            {
                while (true)
                {
                    (Action workItem, ExecutionContext? context) = s_workItems.Take();
                    if (context is null)
                    {
                        workItem();
                    }
                    else
                    {
                        ExecutionContext.Run(context, static (object? state) => ((Action)state!).Invoke(), workItem);
                    }
                }
            })
            {
                // Mark these threads as a background threads so the main method
                // does not wait for all of them to finish to end the process
                IsBackground = true 
            }.Start();
        } 
    }
}

