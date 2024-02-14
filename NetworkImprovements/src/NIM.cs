using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Net;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.GameContent;

public class NIM : ModSystem
{
    public PhysicsManager physicsManager;
    public UDPNetwork udpNetwork;

    public ICoreClientAPI capi;
    public ICoreServerAPI sapi;

    public IClientNetworkChannel clientChannel;
    public IServerNetworkChannel serverChannel;

    public override double ExecuteOrder() => 0;

    public long listener;

    public override void Start(ICoreAPI api)
    {
        // Create the UDP system for sending packets.
        // Starts relevant thread.
        udpNetwork = new UDPNetwork(api, this);

        // Handles all server side physics and sents packets out in sync.
        if (api is ICoreServerAPI serverApi) physicsManager = new PhysicsManager(serverApi, udpNetwork, this);

        RemapClasses(api);
    }

    public static void RemapClasses(ICoreAPI api)
    {
        // Re-map classes so they don't all need to be raplaced in patches.
        ClassRegistry registry = (api.ClassRegistry as ClassRegistryAPI).GetField<ClassRegistry>("registry");

        Dictionary<string, Type> mappings = registry.GetField<Dictionary<string, Type>>("entityBehaviorClassNameToTypeMapping");
        Dictionary<Type, string> mappingsTypeToBehavior = registry.GetField<Dictionary<Type, string>>("entityBehaviorTypeToClassNameMapping");

        mappings.Remove("interpolateposition");
        mappingsTypeToBehavior.Remove(typeof(EntityBehaviorInterpolatePosition));
        api.RegisterEntityBehaviorClass("interpolateposition", typeof(EntityInterpolation));

        mappings.Remove("passivephysics");
        mappingsTypeToBehavior.Remove(typeof(EntityBehaviorPassivePhysics));
        api.RegisterEntityBehaviorClass("passivephysics", typeof(EntityPassivePhysics));

        mappings.Remove("controlledphysics");
        mappingsTypeToBehavior.Remove(typeof(EntityControlledPhysics));
        api.RegisterEntityBehaviorClass("controlledphysics", typeof(EntityControlledPhysics));

        mappings.Remove("playerphysics");
        mappingsTypeToBehavior.Remove(typeof(EntityBehaviorPlayerPhysics));
        api.RegisterEntityBehaviorClass("playerphysics", typeof(EntityPlayerPhysics));
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        ClientMain main = api.World as ClientMain;

        // Make the player not send positions at random intervals. They will now be sent in the player physics.
        List<GameTickListener> listeners = main.GetField<ClientEventManager>("eventManager").GetField<List<GameTickListener>>("GameTickListenersEntity");
        GameTickListener listenerFound = null;
        foreach (GameTickListener listener in listeners)
        {
            if (listener.Millisecondinterval == 100 && listener.Handler.Target is SystemSendPosition)
            {
                listenerFound = listener;
            }
        }

        listeners.Remove(listenerFound);

        clientChannel = capi.Network.RegisterChannel("nim")
            .RegisterMessageType<PositionPacket>()
            .RegisterMessageType<AnimationPacket>()
            .RegisterMessageType<BulkAnimationPacket>()
            .RegisterMessageType<NotificationPacket>()
            .SetMessageHandler<PositionPacket>(HandleTCPPositionPacket)
            .SetMessageHandler<AnimationPacket>(HandleAnimationPacket)
            .SetMessageHandler<BulkAnimationPacket>(HandleBulkAnimationPacket)
            .SetMessageHandler<NotificationPacket>(ClientNotified);

        capi.Event.PlayerJoin += Event_PlayerJoin;
    }

    private void Event_PlayerJoin(IClientPlayer byPlayer)
    {
        clientChannel.SendPacket(new NotificationPacket());

        listener = capi.Event.RegisterGameTickListener(dt =>
        {
            udpNetwork.SendConnectionPacket();
        }, 1000);

        capi.Event.PlayerJoin -= Event_PlayerJoin;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        serverChannel = sapi.Network.RegisterChannel("nim")
            .RegisterMessageType<PositionPacket>()
            .RegisterMessageType<AnimationPacket>()
            .RegisterMessageType<BulkAnimationPacket>()
            .RegisterMessageType<NotificationPacket>()
            .SetMessageHandler<NotificationPacket>(ServerNotified);

        sapi.Event.PlayerDisconnect += Event_PlayerDisconnect;
    }

    private void Event_PlayerDisconnect(IServerPlayer byPlayer)
    {
        IPEndPoint endPoint = udpNetwork.connectedClients.Get(byPlayer);

        if (endPoint != null)
        {
            udpNetwork.endPoints.Remove(endPoint);
        }

        udpNetwork.connectingClients.Remove(byPlayer.Entity.EntityId);
        udpNetwork.connectedClients.Remove(byPlayer);
    }

    public void ClientNotified(NotificationPacket packet)
    {
        // Client notified that it's connected, stop sending connection requests.
        capi.Event.UnregisterGameTickListener(listener);
    }
    
    public void ServerNotified(IServerPlayer fromPlayer, NotificationPacket packet)
    {
        // Server notified client is trying to connect.
        udpNetwork.connectingClients.Add(fromPlayer.Entity.EntityId, fromPlayer);
    }

    public void HandleTCPPositionPacket(PositionPacket packet)
    {
        udpNetwork.HandleSinglePacket(SerializerUtil.Serialize(packet));
    }

    public void HandleAnimationPacket(AnimationPacket packet)
    {
        Entity entity = capi.World.GetEntityById(packet.entityId);

        if (entity == null) return;

        if (entity.Properties?.Client?.LoadedShapeForEntity?.Animations != null)
        {
            float[] speeds = new float[packet.activeAnimationSpeedsCount];
            for (int x = 0; x < speeds.Length; x++)
            {
                speeds[x] = CollectibleNet.DeserializeFloatPrecise(packet.activeAnimationSpeeds[x]);
            }
            entity.OnReceivedServerAnimations(packet.activeAnimations, packet.activeAnimationsCount, speeds);
        }
    }

    public void HandleBulkAnimationPacket(BulkAnimationPacket bulkPacket)
    {
        if (bulkPacket.packets == null) return;

        for (int i = 0; i < bulkPacket.packets.Length; i++)
        {
            AnimationPacket packet = bulkPacket.packets[i];

            Entity entity = capi.World.GetEntityById(packet.entityId);

            if (entity == null) continue;

            if (entity.Properties?.Client?.LoadedShapeForEntity?.Animations != null)
            {
                float[] speeds = new float[packet.activeAnimationSpeedsCount];
                for (int x = 0; x < speeds.Length; x++)
                {
                    speeds[x] = CollectibleNet.DeserializeFloatPrecise(packet.activeAnimationSpeeds[x]);
                }
                entity.OnReceivedServerAnimations(packet.activeAnimations, packet.activeAnimationsCount, speeds);
            }
        }
    }

    readonly Harmony harmony = new("networkimprovements");
    public static bool patched = false;
    public bool localPatched = false;

    public override void StartPre(ICoreAPI api)
    {
        if (!patched)
        {
            harmony.PatchAll();
            localPatched = true;
            patched = true;
        }
    }

    public override void Dispose()
    {
        if (localPatched)
        {
            harmony.UnpatchAll();
            localPatched = false;
            patched = false;
        }

        physicsManager?.Dispose();
        udpNetwork.Dispose();
    }
    
    public static void RemovePhysicsTickable(ICoreAPI api, IPhysicsTickable entityBehavior)
    {
        // Add an entity to the physics ticking system on the server.
        api.ModLoader.GetModSystem<NIM>().physicsManager.toRemove.Enqueue(entityBehavior);
    }

    public static void AddPhysicsTickable(ICoreAPI api, IPhysicsTickable entityBehavior)
    {
        // Remove an entity from the physics ticking system on the server.
        api.ModLoader.GetModSystem<NIM>().physicsManager.toAdd.Enqueue(entityBehavior);
    }
}