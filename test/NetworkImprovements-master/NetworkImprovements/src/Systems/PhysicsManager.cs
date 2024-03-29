﻿using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Common;
using Vintagestory.Server;

// Load balanced on server.
public class PhysicsManager : LoadBalancedTask
{
    public Queue<IPhysicsTickable> toAdd = new();
    public Queue<IPhysicsTickable> toRemove = new();

    // Interval at which physics are ticked.
    public float tickInterval = 1 / 30f;

    public ICoreServerAPI sapi;
    public UDPNetwork udpNetwork;
    public NIM system;

    public ServerMain server;
    public LoadBalancer loadBalancer;

    public long listener;

    public float accumulation = 0;

    // All tickable behaviors.
    public List<IPhysicsTickable> tickables = new();
    public int tickableCount = 0;

    public ServerSystemEntitySimulation es;

    public PhysicsManager(ICoreServerAPI sapi, UDPNetwork udpNetwork, NIM system)
    {
        this.sapi = sapi;
        this.udpNetwork = udpNetwork;
        this.system = system;

        int threadCount = Math.Min(8, MagicNum.MaxPhysicsThreads);

        server = sapi.World as ServerMain;
        loadBalancer = new LoadBalancer(this, ServerMain.Logger);
        loadBalancer.CreateDedicatedThreads(threadCount, "physicsManager", server.Serverthreads);

        // Register to tick every tick.
        listener = server.RegisterGameTickListener(ServerTick, 1);

        es = server.GetField<ServerSystem[]>("Systems")[6] as ServerSystemEntitySimulation;

        GrabFields();

        rateModifier = 1;
    }

    private List<KeyValuePair<Entity, EntityDespawnData>> entitiesNowOutOfRange;
    private List<Entity> entitiesNowInRange;
    private List<Entity> entitiesFullUpdate;
    private List<Entity> entitiesPartialUpdate;
    private List<Entity> entitiesPositionUpdate;
    private List<Entity> entitiesPositionMinimalUpdate;
    private List<Entity> entitiesFullDebugUpdate;
    private List<Entity> entitiesPartialDebugUpdate;

    private CachingConcurrentDictionary<long, Entity> loadedEntities;

    public List<Entity> entitiesPositionMinimalUpdateLowRes = new();

    public void GrabFields()
    {
        entitiesNowOutOfRange = es.GetField<List<KeyValuePair<Entity, EntityDespawnData>>>("entitiesNowOutOfRange");
        entitiesNowInRange = es.GetField<List<Entity>>("entitiesNowInRange");
        entitiesFullUpdate = es.GetField<List<Entity>>("entitiesFullUpdate");
        entitiesPartialUpdate = es.GetField<List<Entity>>("entitiesPartialUpdate");
        entitiesPositionUpdate = es.GetField<List<Entity>>("entitiesPositionupdate");
        entitiesPositionMinimalUpdate = es.GetField<List<Entity>>("entitiesPositionMinimalupdate");
        entitiesFullDebugUpdate = es.GetField<List<Entity>>("entitiesFullDebugUpdate");
        entitiesPartialDebugUpdate = es.GetField<List<Entity>>("entitiesPartialDebugUpdate");

        loadedEntities = server.GetField<CachingConcurrentDictionary<long, Entity>>("LoadedEntities");
    }

    public int tick = 0;

    public List<PositionPacket> positionsToSend = new();
    public List<MinPositionPacket> minPositionsToSend = new();

    // Entities > 50 distance away (configurable) only send positions 5 times / second.
    public void PartitionEntities()
    {
        foreach (ConnectedClient client in server.Clients.Values)
        {
            foreach (long id in client.TrackedEntities.Keys)
            {
                Entity entity = sapi.World.GetEntityById(id);

                if (entity is EntityPlayer || entity == null) continue;

                if (entity.ServerPos.DistanceTo(client.Entityplayer.ServerPos) > 50)
                {
                    client.TrackedEntities[id] = false;
                    continue;
                }

                client.TrackedEntities[id] = true;
            }
        }
    }

    // Update positions on UDP.
    public void UpdatePositions()
    {
        tick++;

        // Every second, update stationary entities.
        bool forceUpdate = false;
        bool lowResUpdate = false;

        if (tick % 15 == 0)
        {
            forceUpdate = true;
        }

        if (tick % 5 == 0)
        {
            lowResUpdate = true;
        }

        foreach (ConnectedClient client in server.Clients.Values)
        {
            if (client.State != EnumClientState.Connected && client.State != EnumClientState.Playing) continue;

            entitiesPositionUpdate.Clear();
            entitiesPositionMinimalUpdate.Clear();
            entitiesPositionMinimalUpdateLowRes.Clear();

            foreach (long id in client.TrackedEntities.Keys)
            {
                Entity entity = sapi.World.GetEntityById(id);

                if (entity is EntityPlayer || entity == null) continue;

                EntityAgent entityAgent = entity as EntityAgent;

                if ((entity.AnimManager != null && entity.AnimManager.AnimationsDirty) || entity.IsTeleport)
                {
                    entitiesPositionUpdate.Add(entity);
                }
                else if (forceUpdate || !entity.ServerPos.BasicallySameAs(entity.PreviousServerPos, 0.0001) || (entityAgent != null && entityAgent.Controls.Dirty))
                {
                    if (client.TrackedEntities[id])
                    {
                        entitiesPositionMinimalUpdate.Add(entity);
                        continue;
                    }
                    else if (lowResUpdate)
                    {
                        entitiesPositionMinimalUpdateLowRes.Add(entity);
                    }
                }
            }

            // Send at most 100 position updates per packet (~5kb).
            int size = 0;
            positionsToSend.Clear();
            minPositionsToSend.Clear();

            BulkAnimationPacket bulkAnimationPacket = new()
            {
                packets = new AnimationPacket[entitiesPositionUpdate.Count]
            };

            int i = 0;
            foreach (Entity entity in entitiesPositionUpdate)
            {
                positionsToSend.Add(new PositionPacket(entity, false));
                size++;

                bulkAnimationPacket.packets[i++] = new AnimationPacket(entity);

                if (size > 100)
                {
                    BulkPositionPacket bulkPositionPacket = new()
                    {
                        packets = positionsToSend.ToArray(),
                        minPackets = Array.Empty<MinPositionPacket>()
                    };
                    udpNetwork.SendBulkPositionPacket(bulkPositionPacket, client.Player);
                    positionsToSend.Clear();
                    size = 0;
                }
            }

            foreach (Entity entity in entitiesPositionMinimalUpdate)
            {
                minPositionsToSend.Add(new MinPositionPacket(entity, false));
                size++;

                if (size > 100)
                {
                    BulkPositionPacket bulkPositionPacket = new()
                    {
                        packets = positionsToSend.ToArray(),
                        minPackets = minPositionsToSend.ToArray()
                    };
                    udpNetwork.SendBulkPositionPacket(bulkPositionPacket, client.Player);

                    positionsToSend.Clear();
                    minPositionsToSend.Clear();

                    size = 0;
                }
            }

            foreach (Entity entity in entitiesPositionMinimalUpdateLowRes)
            {
                minPositionsToSend.Add(new MinPositionPacket(entity, true));
                size++;

                if (size > 100)
                {
                    BulkPositionPacket bulkPositionPacket = new()
                    {
                        packets = positionsToSend.ToArray(),
                        minPackets = minPositionsToSend.ToArray()
                    };
                    udpNetwork.SendBulkPositionPacket(bulkPositionPacket, client.Player);

                    positionsToSend.Clear();
                    minPositionsToSend.Clear();

                    size = 0;
                }
            }

            if (size > 0)
            {
                BulkPositionPacket bulkPositionPacket = new()
                {
                    packets = positionsToSend.ToArray(),
                    minPackets = minPositionsToSend.ToArray()
                };
                udpNetwork.SendBulkPositionPacket(bulkPositionPacket, client.Player);
            }

            system.serverChannel.SendPacket(bulkAnimationPacket, client.Player);
        }
        
        foreach (Entity entity in loadedEntities.Values)
        {
            if (entity is EntityPlayer) continue;

            entity.PreviousServerPos.SetFrom(entity.ServerPos);

            if (entity.AnimManager != null) entity.AnimManager.AnimationsDirty = false;

            entity.IsTeleport = false;
        }

        if (tick % 45 == 0)
        {
            PartitionEntities();
        }
    }

    // Update attributes on TCP.
    public void UpdateAttributes()
    {
        foreach (ConnectedClient client in server.Clients.Values)
        {
            if (client.State != EnumClientState.Connected && client.State != EnumClientState.Playing)
            {
                continue;
            }

            entitiesNowInRange.Clear();
            entitiesNowOutOfRange.Clear();
            entitiesPositionUpdate.Clear();
            entitiesPositionMinimalUpdate.Clear();
            entitiesFullUpdate.Clear();
            entitiesPartialUpdate.Clear();
            entitiesFullDebugUpdate.Clear();
            entitiesPartialDebugUpdate.Clear();

            foreach (Entity entity in loadedEntities.Values)
            {
                bool trackedByClient = client.TrackedEntities.ContainsKey(entity.EntityId);
                bool noChunk = !client.DidSendChunk(entity.InChunkIndex3d) && entity.EntityId != client.Player.Entity.EntityId;
                if (noChunk && !trackedByClient) continue;

                if (entity == client.Entityplayer)
                {
                    if (entity.WatchedAttributes.AllDirty)
                    {
                        entitiesFullUpdate.Add(entity);
                    }
                    else if (entity.WatchedAttributes.PartialDirty)
                    {
                        entitiesPartialUpdate.Add(entity);
                    }

                    continue;
                }

                bool inRange = entity.ServerPos.InRangeOf(client.Entityplayer.ServerPos, es.GetField<int>("trackingRangeSq")) && !noChunk;
                if (!trackedByClient && !inRange)
                {
                    continue;
                }

                if (trackedByClient && !inRange)
                {
                    client.TrackedEntities.Remove(entity.EntityId);
                    entitiesNowOutOfRange.Add(new KeyValuePair<Entity, EntityDespawnData>(entity, new EntityDespawnData
                    {
                        Reason = EnumDespawnReason.OutOfRange
                    }));
                    continue;
                }

                if (!trackedByClient && inRange && client.TrackedEntities.Count < MagicNum.TrackedEntitiesPerClient)
                {
                    client.TrackedEntities.Add(entity.EntityId, value: false);
                    entitiesNowInRange.Add(entity);
                    continue;
                }

                if (entity.WatchedAttributes.AllDirty)
                {
                    entitiesFullUpdate.Add(entity);
                }
                else if (entity.WatchedAttributes.PartialDirty)
                {
                    entitiesPartialUpdate.Add(entity);
                }

                if (server.Config.EntityDebugMode)
                {
                    if (entity.DebugAttributes.AllDirty)
                    {
                        entitiesFullDebugUpdate.Add(entity);
                    }
                    else if (entity.DebugAttributes.PartialDirty)
                    {
                        entitiesPartialDebugUpdate.Add(entity);
                    }
                }
            }

            foreach (Entity nowInRange in entitiesNowInRange)
            {
                if (nowInRange is EntityPlayer entityPlayer)
                {
                    server.PlayersByUid.TryGetValue(entityPlayer.PlayerUID, out var value);
                    if (value != null)
                    {
                        server.SendPacket(client.Id, ((ServerWorldPlayerData)value.WorldData).ToPacketForOtherPlayers(value));
                    }
                }

                server.SendPacket(client.Id, ServerPackets.GetFullEntityPacket(nowInRange));
            }

            if (entitiesFullUpdate.Count > 0 || entitiesPartialUpdate.Count > 0)
            {
                server.SendPacket(client.Id, ServerPackets.GetBulkEntityAttributesPacket(entitiesFullUpdate, entitiesPartialUpdate, entitiesPositionUpdate, entitiesPositionMinimalUpdate));
            }

            if (server.Config.EntityDebugMode && (entitiesFullDebugUpdate.Count > 0 || entitiesPartialDebugUpdate.Count > 0))
            {
                server.SendPacket(client.Id, ServerPackets.GetBulkEntityDebugAttributesPacket(entitiesFullDebugUpdate, entitiesPartialDebugUpdate));
            }

            if (entitiesNowOutOfRange.Count > 0)
            {
                server.SendPacket(client.Id, ServerPackets.GetEntityDespawnPacket(entitiesNowOutOfRange));
            }
        }

        foreach (Entity entity in loadedEntities.Values)
        {
            entity.WatchedAttributes.MarkClean();

            if (entity is EntityAgent agent) agent.Controls.Dirty = false;
        }
    }

    // Add an entity.
    public void AddPhysicsTickables()
    {
        while (toAdd.Count > 0)
        {
            tickables.Add(toAdd.Dequeue());
            tickableCount = tickables.Count;
        }
    }

    // Remove and entity.
    public void RemovePhysicsTickables()
    {
        while (toRemove.Count > 0)
        {
            tickables.Remove(toRemove.Dequeue());
            tickableCount = tickables.Count;
        }
    }

    // Ticks physics at fixed interval and skips ticks when overloaded.
    public void ServerTick(float dt)
    {
        AddPhysicsTickables();
        RemovePhysicsTickables();

        accumulation += dt;

        if (accumulation > 1000)
        {
            accumulation = 0;
            ServerMain.Logger.Warning("Skipping 1000ms of physics ticks. Overloaded.");
        }

        while (accumulation > tickInterval)
        {
            accumulation -= tickInterval;
            DoServerTick();
        }
    }

    public int currentTick;
    public static float rateModifier = 1;

    public void DoServerTick()
    {
        // Send spawns every tick?

        float adjustedRate = tickInterval * rateModifier;

        currentTick++;
        if (currentTick % 6 == 0)
        {
            UpdateAttributes();
        }
        if (currentTick % 2 == 0)
        {
            UpdatePositions();
        }

        if (tickableCount == 0) return;

        loadBalancer.SynchroniseWorkToMainThread();

        // Processes post-ticks at a thread-safe level.
        foreach (IPhysicsTickable tickable in tickables)
        {
            tickable.FlagTickDone = 0;

            try
            {
                tickable.AfterPhysicsTick(adjustedRate);
            }
            catch (Exception e)
            {
                ServerMain.Logger.Error(e);
            }
        }
    }

    public void DoWork()
    {
        float adjustedRate = tickInterval * rateModifier;

        foreach (IPhysicsTickable tickable in tickables)
        {
            if (AsyncHelper.CanProceedOnThisThread(ref tickable.FlagTickDone))
            {
                tickable.OnPhysicsTick(adjustedRate);
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

    public void Dispose()
    {
        server?.UnregisterGameTickListener(listener);

        tickables.Clear(); // Race condition?
    }
}