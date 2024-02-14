using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Server;

public class UDPNetwork
{
    public ICoreClientAPI capi;
    public ICoreServerAPI sapi;

    public ClientMain client;
    public ServerMain server;

    public Action<byte[]>[] clientHandlers = new Action<byte[]>[128];
    public Action<byte[], IServerPlayer>[] serverHandlers = new Action<byte[], IServerPlayer>[128];

    public bool connected = false;

    public IPAddress serverIp;
    public int serverPort = 42420;

    // Sending data.
    public UdpClient udpClient;

    public NIM nim;

    public UDPNetwork(ICoreAPI api, NIM nim)
    {
        this.nim = nim;

        if (api is ICoreClientAPI clientApi)
        {
            clientHandlers[1] = HandleBulkPacket;
            clientHandlers[2] = HandleSinglePacket;

            capi = clientApi;
            client = capi.World as ClientMain;
            client.RegisterGameTickListener(ClientTick, 15);

            GuiScreenRunningGame gui = client.GetField<GuiScreenRunningGame>("ScreenRunningGame");
            if (client.GetField<bool>("IsSingleplayer"))
            {
                serverIp = IPAddress.Loopback;
            }
            else
            {
                ServerConnectData connectData = client.GetField<ServerConnectData>("Connectdata");

                IPAddress hostAddress = null;
                if (connectData.Host == "localhost")
                {
                    hostAddress = IPAddress.Loopback;
                }
                else
                {
                    IPAddress[] hostAddresses = Dns.GetHostAddresses(connectData.Host);
                    hostAddress = hostAddresses.FirstOrDefault();
                }

                serverIp = hostAddress;

                serverPort = connectData.Port;
            }

            udpClient = new();
            udpClient.Connect(serverIp, serverPort);

            ListenClient();
        }

        if (api is ICoreServerAPI serverApi)
        {
            int ownPort = (serverApi.World as ServerMain).GetField<int>("CurrentPort");

            if (ownPort == 0) ownPort = serverPort;

            Console.WriteLine($"Listening on port {ownPort} on UDP");

            udpClient = new UdpClient(ownPort);

            serverHandlers[1] = HandlePlayerPosition;
            serverHandlers[2] = HandleMountPosition;

            sapi = serverApi;
            server = sapi.World as ServerMain;
            server.RegisterGameTickListener(ServerTick, 15);

            // Every 2 seconds, validate packets.
            server.RegisterGameTickListener(ValidatePackets, 2000);

            ListenServer();
        }
    }

    public object messageLock = new();
    public Queue<UDPPacket> clientPacketQueue = new();
    public Queue<UDPPacket> serverPacketQueue = new();

    public Dictionary<long, IServerPlayer> connectingClients = new();
    public Dictionary<IServerPlayer, IPEndPoint> connectedClients = new();
    public Dictionary<IPEndPoint, IServerPlayer> endPoints = new();

    // Listen for messages and enqueue.

    public void ListenClient()
    {
        byte[] dataBuffer;

        Task task = new(() =>
        {
            IPEndPoint endPoint = new(0, 0);

            try
            {
                while (true)
                {
                    dataBuffer = udpClient.Receive(ref endPoint);

                    UDPPacket packet = SerializerUtil.Deserialize<UDPPacket>(dataBuffer);

                    if (packet != null)
                    {
                        lock (messageLock)
                        {
                            clientPacketQueue.Enqueue(packet);
                        }
                    }
                }
            }
            catch
            {

            }
        });

        Thread thread = new(task.Start)
        {
            Priority = ThreadPriority.Normal
        };

        thread.Start();
    }

    public void ListenServer()
    {
        byte[] dataBuffer;

        Task task = new(() =>
        {
            IPEndPoint endPoint = new(0, 0);

            try
            {
                while (true)
                {
                    dataBuffer = udpClient.Receive(ref endPoint);

                    UDPPacket packet = SerializerUtil.Deserialize<UDPPacket>(dataBuffer);

                    if (packet?.id == 0)
                    {
                        HandleConnectionRequest(packet.data, new IPEndPoint(endPoint.Address.Address, endPoint.Port));
                    }
                    else
                    {
                        IServerPlayer sp = endPoints.Get(endPoint);
                        if (sp != null && packet != null)
                        {
                            packet.player = sp;

                            lock (messageLock)
                            {
                                serverPacketQueue.Enqueue(packet);
                            }
                        }
                    }
                }
            }
            catch
            {

            }
        });

        Thread thread = new(task.Start)
        {
            Priority = ThreadPriority.Normal
        };

        thread.Start();
    }

    // Process packets every tick.

    public void ClientTick(float dt)
    {
        lock (messageLock)
        {
            while (clientPacketQueue.Count > 0)
            {
                UDPPacket packet = clientPacketQueue.Dequeue();
                clientHandlers[packet.id](packet.data);
            }
        }
    }

    public void ServerTick(float dt)
    {
        lock (messageLock)
        {
            while (serverPacketQueue.Count > 0)
            {
                UDPPacket packet = serverPacketQueue.Dequeue();
                serverHandlers[packet.id](packet.data, packet.player);
            }
        }
    }

    public void HandleSinglePacket(byte[] bytes)
    {
        PositionPacket packet = SerializerUtil.Deserialize<PositionPacket>(bytes);

        if (packet == null) return;

        Entity entity = client.GetEntityById(packet.entityId);
        if (entity == null) return;

        EntityPos pos = entity.ServerPos;

        // Tick info.
        int currentTick = entity.Attributes.GetInt("tick", 0);

        if (packet.tick <= currentTick) return;
        entity.Attributes.SetInt("tickDiff", Math.Min(packet.tick - currentTick, 5));
        entity.Attributes.SetInt("tick", packet.tick);

        if (packet.x != 0) pos.X = packet.x;
        if (packet.y != 0) pos.Y = packet.y;
        if (packet.z != 0) pos.Z = packet.z;

        if (packet.yaw != 0) pos.Yaw = packet.yaw;
        if (packet.pitch != 0) pos.Pitch = packet.pitch;
        if (packet.roll != 0) pos.Roll = packet.roll;

        pos.Motion.Set(packet.motionX, packet.motionY, packet.motionZ);

        if (entity is EntityAgent agent)
        {
            if (packet.headYaw != 0) pos.HeadYaw = packet.headYaw;
            if (packet.headPitch != 0) pos.HeadPitch = packet.headPitch;
            if (packet.bodyYaw != 0) agent.BodyYawServer = packet.bodyYaw;

            // Sets main controls, then server controls if not the player.
            agent.Controls.FromInt(packet.controls & 0x210);
            if (agent.EntityId != client.EntityPlayer.EntityId)
            {
                agent.ServerControls.FromInt(packet.controls);
            }
        }

        entity.OnReceivedServerPos(packet.teleport);
    }

    // Receive a bulk packet on the client.
    public void HandleBulkPacket(byte[] bytes)
    {
        BulkPositionPacket bulkPacket = SerializerUtil.Deserialize<BulkPositionPacket>(bytes);

        if (bulkPacket.packets != null)
        {
            foreach (PositionPacket packet in bulkPacket.packets)
            {
                Entity entity = client.GetEntityById(packet.entityId);
                if (entity == null) continue;

                EntityPos pos = entity.ServerPos;

                // Tick info.
                int currentTick = entity.Attributes.GetInt("tick", 0);

                if (currentTick == 0)
                {
                    entity.Attributes.SetInt("tick", packet.tick);
                    continue;
                }

                if (packet.tick <= currentTick) continue;
                entity.Attributes.SetInt("tickDiff", Math.Min(packet.tick - currentTick, 5));
                entity.Attributes.SetInt("tick", packet.tick);

                if (packet.x != 0) pos.X = packet.x;
                if (packet.y != 0) pos.Y = packet.y;
                if (packet.z != 0) pos.Z = packet.z;

                if (packet.yaw != 0) pos.Yaw = packet.yaw;
                if (packet.pitch != 0) pos.Pitch = packet.pitch;
                if (packet.roll != 0) pos.Roll = packet.roll;

                pos.Motion.Set(packet.motionX, packet.motionY, packet.motionZ);

                if (entity is EntityAgent agent)
                {
                    if (packet.headYaw != 0) pos.HeadYaw = packet.headYaw;
                    if (packet.headPitch != 0) pos.HeadPitch = packet.headPitch;
                    if (packet.bodyYaw != 0) agent.BodyYawServer = packet.bodyYaw;

                    // Sets main controls, then server controls if not the player.
                    agent.Controls.FromInt(packet.controls & 0x210);
                    if (agent.EntityId != client.EntityPlayer.EntityId)
                    {
                        agent.ServerControls.FromInt(packet.controls);
                    }
                }

                entity.OnReceivedServerPos(packet.teleport);
            }
        }

        if (bulkPacket.minPackets != null)
        {
            foreach (MinPositionPacket packet in bulkPacket.minPackets)
            {
                Entity entity = client.GetEntityById(packet.entityId);
                if (entity == null) continue;
                EntityPos pos = entity.ServerPos;

                // Tick info.
                int currentTick = entity.Attributes.GetInt("tick", 0);

                if (currentTick == 0)
                {
                    entity.Attributes.SetInt("tick", packet.tick);
                    continue;
                }

                if (packet.tick <= currentTick) continue;
                entity.Attributes.SetInt("tickDiff", Math.Min(packet.tick - currentTick, 10));
                entity.Attributes.SetInt("tick", packet.tick);

                if (packet.x != 0) pos.X = packet.x;
                if (packet.y != 0) pos.Y = packet.y;
                if (packet.z != 0) pos.Z = packet.z;

                if (packet.yaw != 0) pos.Yaw = packet.yaw;
                if (packet.pitch != 0) pos.Pitch = packet.pitch;
                if (packet.roll != 0) pos.Roll = packet.roll;

                if (entity is EntityAgent agent)
                {
                    if (packet.headYaw != 0) pos.HeadYaw = packet.headYaw;
                    if (packet.headPitch != 0) pos.HeadPitch = packet.headPitch;
                    if (packet.bodyYaw != 0) agent.BodyYawServer = packet.bodyYaw;

                    // Sets main controls, then server controls if not the player.
                    agent.Controls.FromInt(packet.controls & 0x210);
                    if (agent.EntityId != client.EntityPlayer.EntityId)
                    {
                        agent.ServerControls.FromInt(packet.controls);
                    }
                }

                entity.OnReceivedServerPos(false);
            }
        }
    }

    public Dictionary<IServerPlayer, int> packetsReceived = new();

    public void ValidatePackets(float dt)
    {
        float interval = 1 / 15f;
        int maxPackets = (int)(dt / interval);

        foreach (KeyValuePair<IServerPlayer, int> pair in packetsReceived)
        {
            if (pair.Value > maxPackets * 1.5)
            {
                sapi.BroadcastMessageToAllGroups($"Player {pair.Key} is sending too many packets!", EnumChatType.Notification);
                pair.Key.Disconnect("Too many packets");
            }
        }

        packetsReceived.Clear();
    }

    public void HandlePlayerPosition(byte[] bytes, IServerPlayer player)
    {
        // Log how many packets have been received by this player.
        int packetsNum = packetsReceived.GetValueOrDefault(player, 0);
        packetsReceived[player] = packetsNum + 1;

        // Get packet.
        PositionPacket packet = SerializerUtil.Deserialize<PositionPacket>(bytes);
        if (packet == null) return;

        // Invalidate packets for other entities. This can only move the player so it's known which one.
        EntityPlayer entity = player.Entity;
        ServerPlayer serverPlayer = player as ServerPlayer;

        // Get current version of the entity position, if the packet being sent before the player was notified of this discard it.
        int version = entity.WatchedAttributes.GetInt("positionVersionNumber");
        if (packet.positionVersion < version) return;
        serverPlayer.LastReceivedClientPosition = server.ElapsedMilliseconds;

        // Increase the entity's current tick by 1.
        int currentTick = entity.Attributes.GetInt("tick", 0);
        currentTick++;
        entity.Attributes.SetInt("tick", currentTick);

        // Set entity server and local position.
        entity.ServerPos.SetFromPacket(packet, entity);
        entity.Pos.SetFromPacket(packet, entity);

        // Call physics event.
        foreach (EntityBehavior behavior in entity.SidedProperties.Behaviors)
        {
            // Call on received client pos event.
            if (behavior is IRemotePhysics remote)
            {
                remote.OnReceivedClientPos(version, 1);
                break;
            }
        }

        // Broadcast position to other players.
        PositionPacket positionPacket = new(entity, currentTick);
        byte[] packetBytes = MakeUDPPacket(2, positionPacket);
        entity.IsTeleport = false;

        foreach (IServerPlayer sp in connectedClients.Keys)
        {
            if (sp == player) continue;

            if (server.Clients[sp.ClientId].TrackedEntities.TryGetValue(entity.EntityId, out bool _))
            {
                SendToClient(packetBytes, sp);
            }
        }
        
        if (entity.AnimManager?.AnimationsDirty == true)
        {
            AnimationPacket animationPacket = new(entity);

            foreach (IServerPlayer sp in connectedClients.Keys)
            {
                if (sp == player) continue;

                if (server.Clients[sp.ClientId].TrackedEntities.TryGetValue(entity.EntityId, out bool _))
                {
                    nim.serverChannel.SendPacket(animationPacket, sp);
                }
            }

            entity.AnimManager.AnimationsDirty = false;
        }
    }

    public void HandleMountPosition(byte[] bytes, IServerPlayer player)
    {
        // Get packet.
        PositionPacket packet = SerializerUtil.Deserialize<PositionPacket>(bytes);
        if (packet == null) return;

        // Controlled client id is set on the server.
        Entity entity = sapi.World.GetEntityById(packet.entityId);

        //if (entity.Attributes.GetInt("controller") != player.ClientId) return;

        // Get current version of the entity position, if the packet being sent before the player was notified of this discard it.
        int version = entity.WatchedAttributes.GetInt("positionVersionNumber");
        if (packet.positionVersion < version) return;

        // Increase the entity's current tick by 1.
        int currentTick = entity.Attributes.GetInt("tick", 0);
        currentTick++;
        entity.Attributes.SetInt("tick", currentTick);

        // Set entity server and local position.
        entity.ServerPos.SetFromPacket(packet, entity);
        entity.Pos.SetFromPacket(packet, entity);

        // Call physics event.
        foreach (EntityBehavior behavior in entity.SidedProperties.Behaviors)
        {
            // Call on received client pos event.
            if (behavior is IRemotePhysics remote)
            {
                remote.OnReceivedClientPos(version, 1);
                break;
            }
        }

        // Broadcast position to other players.
        PositionPacket positionPacket = new(entity, currentTick);
        byte[] packetBytes = MakeUDPPacket(2, positionPacket);
        entity.IsTeleport = false;

        foreach (IServerPlayer sp in connectedClients.Keys)
        {
            if (sp == entity) continue;

            if (server.Clients[sp.ClientId].TrackedEntities.TryGetValue(entity.EntityId, out bool _))
            {
                SendToClient(packetBytes, sp);
            }
        }

        if (entity.AnimManager?.AnimationsDirty == true)
        {
            AnimationPacket animationPacket = new(entity);

            foreach (IServerPlayer sp in connectedClients.Keys)
            {
                // Animations sent to everyone.

                if (server.Clients[sp.ClientId].TrackedEntities.TryGetValue(entity.EntityId, out bool _))
                {
                    nim.serverChannel.SendPacket(animationPacket, sp);
                }
            }

            entity.AnimManager.AnimationsDirty = false;
        }
    }

    public void SendConnectionPacket()
    {
        // Send a packet to the server to tell it you're connecting.
        ConnectionPacket connectionPacket = new(capi.World.Player.Entity.EntityId);
        byte[] data = MakeUDPPacket(0, connectionPacket);
        SendToServer(data);
    }

    public void HandleConnectionRequest(byte[] bytes, IPEndPoint endPoint)
    {
        // Handle player's connection request.

        ConnectionPacket packet = SerializerUtil.Deserialize<ConnectionPacket>(bytes);
        IServerPlayer player = connectingClients.Get(packet.entityId);
        if (player == null || connectedClients.ContainsKey(player)) return;

        // [::ffff:47.219.172.229]:63867 game IP is like this, not raw IP.
        string inputString = player.IpAddress;
        string regexPattern = @"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b";
        string ip = Regex.Match(inputString, regexPattern).Value;

        // Kick illegal connections. IP must match.
        if (endPoint.Address.ToString() != ip)
        {
            Console.WriteLine($"Invalid connection: {endPoint.Address} trying to connect for {ip}");
            return;
        }

        connectingClients.Remove(player.Entity.EntityId);
        connectedClients.Add(player, endPoint);
        endPoints.Add(endPoint, player);

        Console.WriteLine($"Player {player.PlayerName} connected on UDP. Sending notification packet...");

        NotificationPacket notificationPacket = new()
        {
            port = endPoint.Port
        };

        nim.serverChannel.SendPacket(notificationPacket, player);
    }

    public void SendPlayerPacket()
    {
        EntityPlayer player = capi.World.Player.Entity;
        player.BodyYawServer = player.BodyYaw;
        PositionPacket packet = new(player, 0);
        SendToServer(MakeUDPPacket(1, packet));
    }

    public void SendMountPacket(Entity entity)
    {
        PositionPacket packet = new(entity, 0);
        SendToServer(MakeUDPPacket(2, packet));
    }

    public void SendBulkPositionPacket(BulkPositionPacket packet, IServerPlayer player)
    {
        SendToClient(MakeUDPPacket(1, packet), player);
    }

    public void SendToClient(byte[] data, IServerPlayer player)
    {
        IPEndPoint clientEndPoint = connectedClients.Get(player);

        if (clientEndPoint == null) return;

        udpClient.BeginSend(data, data.Length, clientEndPoint, (ar) =>
        {
            udpClient.EndSend(ar);
        }, null);
    }

    public void SendToServer(byte[] data)
    {
        udpClient.BeginSend(data, data.Length, (ar) =>
        {
            udpClient.EndSend(ar);
        }, null);
    }

    public static byte[] MakeUDPPacket<T>(byte id, T toSerialize)
    {
        UDPPacket packet = new(id, SerializerUtil.Serialize(toSerialize));
        return SerializerUtil.Serialize(packet);
    }

    public void Dispose()
    {
        udpClient.Close();
    }
}

public static class EntityPosExtensions
{
    public static void SetFromPacket(this EntityPos pos, PositionPacket packet, Entity entity)
    {
        pos.X = packet.x;
        pos.Y = packet.y;
        pos.Z = packet.z;
        pos.Yaw = packet.yaw;
        pos.Pitch = packet.pitch;
        pos.Roll = packet.roll;
        pos.HeadYaw = packet.headYaw;
        pos.HeadPitch = packet.headPitch;

        if (entity is EntityAgent agent)
        {
            agent.BodyYawServer = packet.bodyYaw;
        }
    }
}