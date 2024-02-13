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

    public override void Start(ICoreAPI api)
    {
        // Create the UDP system for sending packets.
        // Starts relevant thread.
        udpNetwork = new UDPNetwork(api);

        // Create a physics manager on both sides. Sends data to clients on UDP network.
        if (api is ICoreServerAPI serverApi)
        {
            physicsManager = new PhysicsManager(serverApi, udpNetwork, this);
        }

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
            .RegisterMessageType<BulkAnimationPacket>()
            .RegisterMessageType<NotificationPacket>()
            .SetMessageHandler<BulkAnimationPacket>(HandleAnimationPacket)
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

    public long listener;

    // Client notified that it's connected, stop sending connection requests.
    public void ClientNotified(NotificationPacket packet)
    {
        capi.Event.UnregisterGameTickListener(listener);
    }

    // Server notified client is trying to connect.
    public void ServerNotified(IServerPlayer fromPlayer, NotificationPacket packet)
    {
        udpNetwork.connectingClients.Add(fromPlayer.Entity.EntityId, fromPlayer);
    }

    // Animations have to be sent seperately from position due to possible loss.
    public void HandleAnimationPacket(BulkAnimationPacket bulkPacket)
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

    // Add an entity to the physics ticking system on the server.
    public static void RemovePhysicsTickable(ICoreAPI api, IPhysicsTickable entityBehavior)
    {
        api.ModLoader.GetModSystem<NIM>().physicsManager.toRemove.Enqueue(entityBehavior);
    }

    // Remove an entity from the physics ticking system on the server.
    public static void AddPhysicsTickable(ICoreAPI api, IPhysicsTickable entityBehavior)
    {
        api.ModLoader.GetModSystem<NIM>().physicsManager.toAdd.Enqueue(entityBehavior);
    }
}