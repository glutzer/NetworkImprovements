using HarmonyLib;
using ProtoBuf;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Server;

/// <summary>
/// Server-authorative entity updates for both client/server.
/// Initially 20 TPS.
/// </summary>
public class NIM : ModSystem
{
    public PhysicsManager physicsManager;

    public ICoreClientAPI capi;
    public ICoreServerAPI sapi;

    public GameTickListener clientUpdateListener;
    public GameTickListener serverUpdateListener;

    public IClientNetworkChannel clientChannel;
    public IServerNetworkChannel serverChannel;

    public int tickrate = 20;

    public override double ExecuteOrder() => 0;

    public override void Start(ICoreAPI api)
    {
        api.RegisterEntityBehaviorClass("EntityInterpolation", typeof(EntityInterpolation));

        api.RegisterEntityBehaviorClass("EntityControlledPhysics", typeof(EntityControlledPhysics));

        api.RegisterEntityBehaviorClass("EntityPassivePhysics", typeof(EntityPassivePhysics));

        api.RegisterEntityBehaviorClass("EntityCollisionChecker", typeof(EntityCollisionChecker));

        api.RegisterEntityBehaviorClass("EntityPlayerPhysics", typeof(EntityPlayerPhysics));

        physicsManager = new PhysicsManager(api);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        ClientMain main = api.World as ClientMain;

        List<GameTickListener> listeners = main.GetField<ClientEventManager>("eventManager").GetField<List<GameTickListener>>("GameTickListenersEntity");
        GameTickListener listenerFound = null;
        foreach (GameTickListener listener in listeners)
        {
            if (listener.Millisecondinterval == 100 && listener.Handler.Target is SystemSendPosition)
            {
                listenerFound = listener;
                listener.Millisecondinterval = 100;
            }
        }

        clientUpdateListener = listenerFound;

        clientChannel = capi.Network.RegisterChannel("nim")
            .RegisterMessageType<TickrateMessage>()
            .SetMessageHandler<TickrateMessage>(OnTickrateReceived);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        ServerMain main = api.World as ServerMain;

        //Set the server sending entity position updates to the client to a new rate
        List<GameTickListener> listeners = main.EventManager.GetField<List<GameTickListener>>("GameTickListenersEntity");
        GameTickListener listenerFound = null;
        foreach (GameTickListener listener in listeners)
        {
            if (listener.Millisecondinterval == 200 && listener.Handler.Target is ServerSystemEntitySimulation)
            {
                listenerFound = listener;
                listener.Millisecondinterval = 100;
            }
        }

        serverUpdateListener = listenerFound;

        serverChannel = sapi.Network.RegisterChannel("nim")
            .RegisterMessageType<TickrateMessage>();

        sapi.Event.PlayerJoin += UpdateTickrates;

        sapi.RegisterCommand(new TickrateCommand(this));
    }

    public void UpdateTickrates(IServerPlayer byPlayer)
    {
        serverChannel.BroadcastPacket(new TickrateMessage()
        {
            tickrate = tickrate
        });
    }

    public void OnTickrateReceived(TickrateMessage packet)
    {
        tickrate = packet.tickrate;
        clientUpdateListener.Millisecondinterval = 1000 / tickrate;
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
    }

    public static void AddPhysicsTickable(ICoreAPI api, PhysicsTickable entityBehavior)
    {
        api.ModLoader.GetModSystem<NIM>().physicsManager.AddPhysicsTickable(entityBehavior);
    }

    public static void RemovePhysicsTickable(ICoreAPI api, PhysicsTickable entityBehavior)
    {
        api.ModLoader.GetModSystem<NIM>().physicsManager.RemovePhysicsTickable(entityBehavior);
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
public class TickrateMessage
{
    public int tickrate;
}

public class TickrateCommand : ServerChatCommand
{
    NIM nim;

    public TickrateCommand(NIM nim)
    {
        this.nim = nim;
        Command = "nimtickrate";
        Description = "Changes rate of entity updates";
        Syntax = ".nimtickrate";
        RequiredPrivilege = Privilege.ban;
    }

    public override void CallHandler(IPlayer player, int groupId, CmdArgs args)
    {
        try
        {
            nim.tickrate = args[0].ToInt();
            nim.serverUpdateListener.Millisecondinterval = 1000 / nim.tickrate;
            nim.UpdateTickrates(null);
            nim.sapi.SendMessage(player, 0, $"Tickrate set to {nim.tickrate}.", EnumChatType.CommandSuccess);
        }
        catch
        {

        }
    }
}