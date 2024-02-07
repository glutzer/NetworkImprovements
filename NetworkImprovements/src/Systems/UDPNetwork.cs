using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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

    public UDPNetwork(ICoreAPI api)
    {
        if (api is ICoreClientAPI clientApi)
        {
            clientHandlers[1] = HandleBulkPacket;

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

    // LISTEN FOR MESSAGES AND ENQUEUE.

    public void ListenClient()
    {
        udpClient.BeginReceive(new AsyncCallback(ClientReceiveCallback), null);
    }

    public void ClientReceiveCallback(IAsyncResult ar)
    {
        UdpReceiveResult dataBuffer;

        Task.Factory.StartNew(async () => {
            try
            {
                while (true)
                {
                    dataBuffer = await udpClient.ReceiveAsync();

                    UDPPacket packet = SerializerUtil.Deserialize<UDPPacket>(dataBuffer.Buffer);

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
    }

    public void ListenServer()
    {
        UdpReceiveResult dataBuffer;

        Task.Factory.StartNew(async () => {
            try
            {
                while (true)
                {
                    dataBuffer = await udpClient.ReceiveAsync();

                    UDPPacket packet = SerializerUtil.Deserialize<UDPPacket>(dataBuffer.Buffer);

                    if (packet?.id == 0)
                    {
                        HandleConnectionRequest(packet.data, new IPEndPoint(dataBuffer.RemoteEndPoint.Address.Address, dataBuffer.RemoteEndPoint.Port));
                    }
                    else
                    {
                        IServerPlayer sp = endPoints.Get(dataBuffer.RemoteEndPoint);
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
    }

    // PROCESS PACKETS EVERY TICK.

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
                int lastTick = entity.WatchedAttributes.GetInt("lastTick", 0);
                if (packet.tick < lastTick) continue;
                if (lastTick == 0) lastTick = packet.tick - 1;
                entity.WatchedAttributes.SetInt("tickDiff", packet.tick - lastTick);
                entity.WatchedAttributes.SetInt("lastTick", packet.tick);

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
                int lastTick = entity.WatchedAttributes.GetInt("lastTick", 0);
                if (packet.tick < lastTick) continue;
                if (lastTick == 0) lastTick = packet.tick - 1;
                entity.WatchedAttributes.SetInt("tickDiff", packet.tick - lastTick);
                entity.WatchedAttributes.SetInt("lastTick", packet.tick);

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

    // Receive a player packet on the server.
    public void HandlePlayerPosition(byte[] bytes, IServerPlayer player)
    {
        int packetsNum = packetsReceived.GetValueOrDefault(player, 0);
        packetsReceived[player] = packetsNum + 1;

        PositionPacket packet = SerializerUtil.Deserialize<PositionPacket>(bytes);

        if (packet == null) return;

        if (sapi.World.GetEntityById(packet.entityId) is not EntityPlayer entity) return;

        ServerPlayer serverPlayer = entity.Player as ServerPlayer;

        // Check version.
        int version = entity.WatchedAttributes.GetInt("positionVersionNumber");
        if (packet.positionVersion < version) return;

        // Check tick.
        int tick = entity.WatchedAttributes.GetInt("ct");
        if (tick > packet.tick) return;
        entity.WatchedAttributes.SetInt("ct", packet.tick);
        int tickDiff = packet.tick - tick;

        serverPlayer.LastReceivedClientPosition = server.ElapsedMilliseconds;

        // Set entity server position.
        EntityPos pos = entity.ServerPos;

        pos.X = packet.x;
        pos.Y = packet.y;
        pos.Z = packet.z;

        pos.Yaw = packet.yaw;
        pos.Pitch = packet.pitch;
        pos.Roll = packet.roll;

        pos.HeadYaw = packet.headYaw;
        pos.HeadPitch = packet.headPitch;
        entity.BodyYawServer = packet.bodyYaw;

        // Set entity local position.
        entity.Pos.SetFrom(entity.ServerPos);
        entity.Pos.SetAngles(entity.ServerPos);

        // Call physics.
        entity.GetBehavior<EntityPlayerPhysics>().OnReceivedClientPos(version, tickDiff);

        // Broadcast position to other players.
        BulkPositionPacket bulkPositionPacket = new()
        {
            packets = new PositionPacket[1],
            minPackets = Array.Empty<MinPositionPacket>()
        };
        bulkPositionPacket.packets[0] = new(entity, tick);

        byte[] packetBytes = MakePacket(1, bulkPositionPacket);

        foreach (IServerPlayer sp in connectedClients.Keys)
        {
            if (sp.Entity == entity) continue;

            if (sp.Entity.ServerPos.DistanceTo(entity.ServerPos) < 500)
            {
                SendToClient(packetBytes, sp);
            }
        }
        entity.IsTeleport = false;

        if (entity.AnimManager?.AnimationsDirty == true)
        {
            AnimationPacket[] packets = new AnimationPacket[1];
            packets[0] = new AnimationPacket(entity);

            BulkAnimationPacket bulkAnimationPacket = new()
            {
                packets = packets
            };

            foreach (IServerPlayer sp in connectedClients.Keys)
            {
                if (sp.Entity == entity) continue;
                sapi.ModLoader.GetModSystem<NIM>().serverChannel.SendPacket(bulkAnimationPacket, sp);
            }

            entity.AnimManager.AnimationsDirty = false;
        }
    }

    // Send a packet to the server to tell it you're connecting.
    public void SendConnectionPacket()
    {
        ConnectionPacket connectionPacket = new(capi.World.Player.Entity.EntityId);
        byte[] data = MakePacket(0, connectionPacket);
        SendToServer(data);
    }

    // Handle player's connection request.
    public void HandleConnectionRequest(byte[] bytes, IPEndPoint endPoint)
    {
        ConnectionPacket packet = SerializerUtil.Deserialize<ConnectionPacket>(bytes);
        IServerPlayer player = connectingClients.Get(packet.entityId);
        if (player == null || connectedClients.ContainsKey(player)) return;

        // Kick illegal connections. IP must match.
        /*
        if (endPoint.Address.ToString() != player.IpAddress)
        {
            Console.WriteLine($"Invalid connection: {endPoint.Address}, {IPAddress.Parse(player.IpAddress)}, {player.IpAddress}");
            return;
        }
        */

        connectingClients.Remove(player.Entity.EntityId);
        connectedClients.Add(player, endPoint);
        endPoints.Add(endPoint, player);

        Console.WriteLine($"Player {player.PlayerName} connected on UDP. Sending notification packet...");

        NotificationPacket notificationPacket = new()
        {
            port = endPoint.Port
        };

        sapi.ModLoader.GetModSystem<NIM>().serverChannel.SendPacket(notificationPacket, player);
    }

    /// <summary>
    /// Serialize a packet into a UDPPacket with the id.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="id">Id of the packet you want to send.</param>
    /// <param name="toSerialize">Packet object.</param>
    /// <returns></returns>
    public static byte[] MakePacket<T>(byte id, T toSerialize)
    {
        UDPPacket packet = new(id, SerializerUtil.Serialize(toSerialize));
        return SerializerUtil.Serialize(packet);
    }

    /// <summary>
    /// Send own player packet to the server.
    /// </summary>
    /// <param name="tick">Tick number in player physics.</param>
    public void SendPlayerPacket(int tick)
    {
        EntityPlayer player = capi.World.Player.Entity;
        player.BodyYawServer = player.BodyYaw;
        PositionPacket packet = new(player, tick);
        SendToServer(MakePacket(1, packet));
    }

    /// <summary>
    /// Send bulk position packet to a player.
    /// </summary>
    public void SendBulkPositionPacket(BulkPositionPacket packet, IServerPlayer player)
    {
        SendToClient(MakePacket(1, packet), player);
    }

    public void SendToClient(byte[] data, IServerPlayer player)
    {
        IPEndPoint clientEndPoint = connectedClients.Get(player);

        if (clientEndPoint == null)
        {
            Console.WriteLine($"Endpoint null for {player.PlayerName}.");
            return;
        }

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

    public void Dispose()
    {
        udpClient.Close();
    }
}