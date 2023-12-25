using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Server;

/// <summary>
/// Clients tick on main thread. Server load balances physics.
/// </summary>
public class PhysicsManager : LoadBalancedTask
{
    //Interval at which physics are ticked
    public float interval = 1 / 30f; //30 UPS

    public ICoreAPI api;
    public ClientMain client;
    public ServerMain server;

    public long listener;
    public LoadBalancer loadBalancer;

    public float accumulation = 0;

    //All tickable behaviors
    public List<PhysicsTickable> tickables = new();
    public int tickableCount = 0;

    public PhysicsManager(ICoreAPI api)
    {
        this.api = api;
        int threadCount = Math.Min(8, MagicNum.MaxPhysicsThreads);

        if (api is ICoreServerAPI sapi)
        {
            server = sapi.World as ServerMain;
            loadBalancer = new LoadBalancer(this, ServerMain.Logger);
            loadBalancer.CreateDedicatedThreads(threadCount, "physicsManager", server.Serverthreads);
            listener = server.RegisterGameTickListener(ServerTick, 10);
        }

        if (api is ICoreClientAPI capi)
        {
            client = capi.World as ClientMain;
            listener = client.RegisterGameTickListener(ClientTick, 10);
        }
    }

    /// <summary>
    /// Adds something with physics to be ticked.
    /// </summary>
    public void AddPhysicsTickable(PhysicsTickable entityBehavior)
    {
        tickables.Add(entityBehavior);
        tickableCount = tickables.Count;
    }

    /// <summary>
    /// Remove a tickable when entity despawns.
    /// </summary>
    public void RemovePhysicsTickable(PhysicsTickable entityBehavior)
    {
        tickables.Remove(entityBehavior);
        tickableCount = tickables.Count;
    }

    /// <summary>
    /// Fixed interval ticker.
    /// </summary>
    public void ServerTick(float dt)
    {
        accumulation += dt;

        if (accumulation > 5000)
        {
            accumulation -= 5000;
            ServerMain.Logger.Warning("Skipping 5000ms of physics ticks. Overloaded.");
        }

        while (accumulation >= interval)
        {
            accumulation -= interval;
            DoServerTick();
        }
    }

    public void ClientTick(float dt)
    {
        accumulation += dt;

        if (accumulation > 5000)
        {
            accumulation -= 5000;
        }

        while (accumulation >= interval)
        {
            accumulation -= interval;
            DoClientTick();
        }
    }

    public void DoServerTick()
    {
        if (tickableCount == 0) return;

        //Load balances DoWork
        loadBalancer.SynchroniseWorkToMainThread();

        //Processes post-ticks at a thread-safe level
        foreach (PhysicsTickable tickable in tickables)
        {
            tickable.FlagTickDone = 0;

            try
            {
                tickable.AfterPhysicsTick(interval);
            }
            catch (Exception e)
            {
                ServerMain.Logger.Error(e);
            }
        }
    }

    public void DoClientTick()
    {
        if (tickableCount == 0) return;

        foreach (PhysicsTickable tickable in tickables)
        {
            tickable.OnPhysicsTick(interval);
        }

        //Processes post-ticks at a thread-safe level
        foreach (PhysicsTickable tickable in tickables)
        {
            tickable.FlagTickDone = 0;
            tickable.AfterPhysicsTick(interval);
        }
    }

    public void DoWork()
    {
        foreach (PhysicsTickable tickable in tickables)
        {
            if (AsyncHelper.CanProceedOnThisThread(ref tickable.FlagTickDone))
            {
                tickable.OnPhysicsTick(interval);
            }
        }
    }

    public bool ShouldExit()
    {
        return server.stopped;
    }

    public void HandleException(Exception e)
    {
        ServerMain.Logger.Error("Error thrown while ticking physics:\n{0}\n{1}", e.Message, e.StackTrace);
    }

    public void StartWorkerThread(int threadNum)
    {
        try
        {
            while (tickableCount < 120)
            {
                if (ShouldExit())
                {
                    return;
                }

                Thread.Sleep(15);
            }
        }
        catch (Exception)
        {
        }

        loadBalancer.WorkerThreadLoop(threadNum);
    }

    /// <summary>
    /// Dispose when reloading or shutting down.
    /// </summary>
    public void Dispose()
    {
        tickables.Clear();

        if (api is ICoreServerAPI)
        {
            server.UnregisterGameTickListener(listener);
        }
        else
        {
            client.UnregisterGameTickListener(listener);
        }
    }
}