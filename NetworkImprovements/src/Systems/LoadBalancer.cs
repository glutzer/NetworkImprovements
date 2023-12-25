using System.Collections.Generic;
using System.Threading;
using System;
using Vintagestory.API.Common;
using Vintagestory.Common;
using Vintagestory;

/// <summary>
/// Load balancer from internal class.
/// </summary>
public class LoadBalancer
{
    public volatile int threadsWorking;
    public LoadBalancedTask caller;
    public Logger logger;

    public LoadBalancer(LoadBalancedTask caller, Logger logger)
    {
        this.caller = caller;
        this.logger = logger;
    }

    public void CreateDedicatedThreads(int threadCount, string name, List<Thread> threadList)
    {
        for (int i = 2; i <= threadCount; i++)
        {
            Thread item = CreateDedicatedWorkerThread(i, name, threadList);
            threadList?.Add(item);
        }
    }

    public Thread CreateDedicatedWorkerThread(int threadnum, string name, List<Thread> threadlist = null)
    {
        Thread thread = TyronThreadPool.CreateDedicatedThread(delegate
        {
            caller.StartWorkerThread(threadnum);
        }, name + threadnum);
        thread.IsBackground = false;
        thread.Priority = Thread.CurrentThread.Priority;
        return thread;
    }

    public void SynchroniseWorkToMainThread()
    {
        if (Interlocked.CompareExchange(ref threadsWorking, 1, 0) != 0)
        {
            throw new Exception("Thread synchronization problem.");
        }

        try
        {
            caller.DoWork();
        }
        finally
        {
            while (Interlocked.CompareExchange(ref threadsWorking, 0, 1) != 1)
            {
            }
        }
    }

    public void SynchroniseWorkOnWorkerThread(int workerNum, int msToSleep)
    {
        while (Interlocked.CompareExchange(ref threadsWorking, workerNum, workerNum - 1) != workerNum - 1 && !caller.ShouldExit())
        {
            if (msToSleep > 0)
            {
                Thread.Sleep(msToSleep);
            }
        }

        try
        {
            caller.DoWork();
        }
        catch (ThreadAbortException)
        {
            throw;
        }
        catch (Exception e)
        {
            caller.HandleException(e);
        }
        finally
        {
            Interlocked.Decrement(ref threadsWorking);
        }
    }

    public void WorkerThreadLoop(int workerNum, int msToSleep = 1)
    {
        try
        {
            while (!caller.ShouldExit())
            {
                SynchroniseWorkOnWorkerThread(workerNum, msToSleep);
            }
        }
        catch (ThreadAbortException)
        {
        }
        catch (Exception ex2)
        {
            logger.Fatal("Error thrown in worker thread management (this and all higher threads will now stop as a precaution)\n{0}\n{1}", ex2.Message, ex2.StackTrace);
        }
    }
}