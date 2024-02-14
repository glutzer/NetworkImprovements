using System;
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
    public NIM nimSystem;

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
        nimSystem = system;

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

    public List<KeyValuePair<Entity, EntityDespawnData>> entitiesNowOutOfRange;

    public List<Entity> entitiesNowInRange;
    public List<Entity> entitiesFullUpdate;
    public List<Entity> entitiesPartialUpdate;
    public List<Entity> entitiesPositionUpdate;
    public List<Entity> entitiesPositionMinimalUpdate;
    public List<Entity> entitiesFullDebugUpdate;
    public List<Entity> entitiesPartialDebugUpdate;

    public CachingConcurrentDictionary<long, Entity> loadedEntities;

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

    // Entities > 50 distance away (configurable) only send positions 3 times / second.
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

    public HashSet<Entity> tickedEntities = new();

    // Update positions on UDP.
    // Only send positions of entities being controlled by the server (no mounted entities or players).
    // Other positions will be sent once received.
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

                // Controlled id is the entity id of the player entity controlling this. || entity.Attributes.GetInt("cid") != 0 || entity is IMountableSupplier
                if (entity == null || entity is EntityPlayer) continue;

                EntityAgent entityAgent = entity as EntityAgent;

                if ((entity.AnimManager != null && entity.AnimManager.AnimationsDirty) || entity.IsTeleport)
                {
                    entitiesPositionUpdate.Add(entity);
                    tickedEntities.Add(entity);
                }
                else if (forceUpdate || !entity.ServerPos.BasicallySameAs(entity.PreviousServerPos, 0.0001) || (entityAgent != null && entityAgent.Controls.Dirty))
                {
                    tickedEntities.Add(entity);

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
                positionsToSend.Add(new PositionPacket(entity, entity.Attributes.GetInt("tick", 0)));
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
                int entityTick = entity.Attributes.GetInt("tick", 0);

                minPositionsToSend.Add(new MinPositionPacket(entity, entity.Attributes.GetInt("tick", 0)));
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
                int entityTick = entity.Attributes.GetInt("tick", 0);

                minPositionsToSend.Add(new MinPositionPacket(entity, entity.Attributes.GetInt("tick", 0)));
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

            nimSystem.serverChannel.SendPacket(bulkAnimationPacket, client.Player);
        }
        
        foreach (Entity entity in loadedEntities.Values)
        {
            if (entity is EntityAgent agent)
            {
                if (entity is EntityPlayer) continue;
                agent.Controls.Dirty = false;
            }

            entity.PreviousServerPos.SetFrom(entity.ServerPos);

            if (entity.AnimManager != null) entity.AnimManager.AnimationsDirty = false;

            entity.IsTeleport = false;
        }

        // If any position has been set out, an entity has been ticked.
        foreach (Entity entity in tickedEntities)
        {
            entity.Attributes.SetInt("tick", entity.Attributes.GetInt("tick") + 1);
        }

        tickedEntities.Clear();

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

            // Adjust bulk attributes to NOT include positions since they aren't sent here anyways.
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
        }
    }

    // Add an entity to receive physics ticks.
    public void AddPhysicsTickables()
    {
        while (toAdd.Count > 0)
        {
            tickables.Add(toAdd.Dequeue());
            tickableCount = tickables.Count;
        }
    }

    // Remove an entity from ticking.
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

        if (accumulation > 2000)
        {
            accumulation = 0;
            ServerMain.Logger.Warning("Skipping 2000ms of physics ticks. Overloaded.");
        }

        while (accumulation > tickInterval)
        {
            accumulation -= tickInterval;
            DoServerTick();
        }
    }

    public List<Entity> toSendSpawn = new();

    public void SendEntitySpawns()
    {
        float adjustedRate = tickInterval * rateModifier;

        lock (server.EntitySpawnSendQueue)
        {
            if (server.EntitySpawnSendQueue.Count <= 0) return;

            int squareDistance = MagicNum.DefaultEntityTrackingRange * MagicNum.ServerChunkSize * MagicNum.DefaultEntityTrackingRange * MagicNum.ServerChunkSize;

            foreach (ConnectedClient client in server.Clients.Values)
            {
                if ((client.State != EnumClientState.Connected && client.State != EnumClientState.Playing) || client.Entityplayer == null) continue;

                foreach (Entity entity in server.EntitySpawnSendQueue)
                {
                    if (entity.ServerPos.InRangeOf(client.Entityplayer.ServerPos, squareDistance))
                    {
                        client.TrackedEntities[entity.EntityId] = true;
                        toSendSpawn.Add(entity);
                    }
                }

                if (toSendSpawn.Count > 0)
                {
                    server.SendPacket(client.Id, ServerPackets.GetEntitySpawnPacket(toSendSpawn));
                }

                toSendSpawn.Clear();
            }

            foreach (Entity entity in server.EntitySpawnSendQueue)
            {
                entity.packet = null;

                foreach (EntityBehavior behavior in entity.SidedProperties.Behaviors)
                {
                    if (behavior is IPhysicsTickable tickable)
                    {
                        tickable.Ticking = true;

                        tickable.OnPhysicsTick(adjustedRate);
                        tickable.AfterPhysicsTick(adjustedRate);

                        tickable.OnPhysicsTick(adjustedRate);
                        tickable.AfterPhysicsTick(adjustedRate);

                        break;
                    }
                }

                entity.Attributes.SetInt("tick", 2);
            }

            foreach (ConnectedClient client in server.Clients.Values)
            {
                if ((client.State != EnumClientState.Connected && client.State != EnumClientState.Playing) || client.Entityplayer == null) continue;

                foreach (Entity entity in server.EntitySpawnSendQueue)
                {
                    if (entity.ServerPos.InRangeOf(client.Entityplayer.ServerPos, squareDistance))
                    {
                        nimSystem.serverChannel.SendPacket(new PositionPacket(entity, 1), client.Player);
                    }
                }
            }

            server.EntitySpawnSendQueue.Clear();
        }
    }

    public int currentTick;
    public static float rateModifier = 1;

    public void DoServerTick()
    {
        float adjustedRate = tickInterval * rateModifier;

        currentTick++;
        if (currentTick % 6 == 0)
        {
            UpdateAttributes();
        }
        if (currentTick % 2 == 0)
        {
            UpdatePositions();
            SendEntitySpawns();
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